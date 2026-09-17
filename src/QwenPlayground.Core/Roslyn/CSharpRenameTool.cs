using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// Безопасное массовое переименование символа через Roslyn (антидот regex-замен по
/// всему коду). Ядро — чистая функция Solution → (Solution, отчёт) (internal,
/// тестируется на in-memory workspace); тул — тонкая обёртка: Shared-солюшен → ядро →
/// запись файлов на диск → применение к живому workspace. Коллизии имён ловит
/// компилятор: пробный rename применяется в памяти, затронутые проекты компилируются,
/// новые ошибки → отмена (ничего не сохраняется). XML-doc cref'ы не обновляются
/// (косметика, не компиляция) — честно в ответе.
/// </summary>
[Tool("csharp_rename",
    "Rename a C# symbol across the whole solution via Roslyn (safe bulk rename — no regex). " +
    "Updates every reference: call sites, declarations, using directives (for types), " +
    "qualified names. Before saving, the rename is applied in memory and the affected " +
    "projects are compiled — name collisions (e.g. renaming to an existing member or a " +
    "keyword) are rejected with the compiler errors and nothing is changed. " +
    "If several symbols share the name (overloads, same name in different types), pass " +
    "file and line of the declaration to disambiguate; otherwise candidates are listed " +
    "and nothing is changed. XML doc crefs are not updated (cosmetic). " +
    "After a successful rename the test gate (rebuild_self) is the final safety net.", ToolGroup.CSharp)]
public sealed class CSharpRenameTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    [ToolParameter("Current symbol name to rename", Required = true)]
    public string Name { get; set; } = string.Empty;

    [ToolParameter("New name", Required = true)]
    public string NewName { get; set; } = string.Empty;

    [ToolParameter("File of the declaration (relative to workspace root), disambiguates same-named symbols", Required = false)]
    public string? File { get; set; }

    [ToolParameter("1-based line of the declaration (with File)", Required = false)]
    public int? Line { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        var outcome = await CSharpRenameCore.ApplyAsync(solution, Name, NewName, File, Line, cancellationToken);
        if (outcome.Error is not null)
        {
            return outcome.Error;
        }
        if (outcome.ChangedFiles is null)
        {
            return "no changes";
        }

        // 1. Диск (до ApplyChanges: _loadedAt=Now покрывает новые write-time файлов).
        //    System.IO.File явно: File без префикса резолвится в свойство тула.
        foreach (var (document, newText) in outcome.ChangedDocuments)
        {
            if (document.FilePath is not null)
            {
                System.IO.File.WriteAllText(document.FilePath, newText);
            }
        }
        // 2. Живой workspace (следующие Roslyn-инструменты и edit-пре-чек видят новый текст).
        await Service.ApplyChangesAsync(outcome.Modified, cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine($"renamed '{Name}' → '{NewName}' ({outcome.SymbolDescription})");
        builder.AppendLine($"  {outcome.TotalLocations} locations in {outcome.ChangedFiles.Count} files:");
        foreach (var (path, locations) in outcome.ChangedFiles)
        {
            builder.AppendLine($"    {path} ({locations})");
        }
        builder.AppendLine("  diagnostics: clean (pre-save compile check passed)");
        builder.AppendLine("note: XML doc crefs were not updated (cosmetic).");
        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// Ядро rename: чистая функция Solution → (Solution, отчёт). Тестируется на
/// in-memory AdhocWorkspace без прикосновения к живому солюшену.
/// </summary>
internal static class CSharpRenameCore
{
    public sealed class Outcome
    {
        public string? Error { get; init; }
        public IReadOnlyList<(string Description, string Path)>? Candidates { get; init; }
        public Solution? Modified { get; init; }
        public IReadOnlyList<(Document Document, string NewText)>? ChangedDocuments { get; init; }
        public IReadOnlyList<(string Path, int Locations)>? ChangedFiles { get; init; }
        public int TotalLocations { get; init; }
        public string SymbolDescription { get; init; } = string.Empty;
    }

    public static async Task<Outcome> ApplyAsync(
        Solution solution, string name, string newName, string? file, int? line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return new Outcome { Error = "Error: new name is empty." };
        }
        // Резервные ключевые слова блокируем; контекстуальные (async, value, ...)
        // легальны как имена — IsReservedKeyword их не помечает.
        var keywordKind = SyntaxFacts.GetKeywordKind(newName);
        if (keywordKind != SyntaxKind.None && SyntaxFacts.IsReservedKeyword(keywordKind))
        {
            return new Outcome { Error = $"Rename rejected: '{newName}' is a C# keyword." };
        }

        // 1. Декларации (дедуп по месту: FindDeclarationsAsync возвращает metadata-копии
        //    символа из каждого проекта-ссыльщика).
        var declarations = await FindDeclarationsAsync(solution, name, ct);
        if (declarations.Count == 0)
        {
            return new Outcome { Error = $"symbol '{name}' not found" };
        }
        if (file is not null || line is not null)
        {
            var match = declarations.FirstOrDefault(d => MatchesLocation(d, file, line));
            if (match is null)
            {
                return new Outcome { Error = $"declaration of '{name}' not found at {file}:{line}" };
            }
            declarations = new List<ISymbol> { match };
        }
        if (declarations.Count > 1)
        {
            var candidates = declarations
                .Select(d => (
                    Description: Describe(d),
                    Path: FormatLocation(d.Locations.FirstOrDefault(l => l.IsInSource))))
                .ToList();
            return new Outcome
            {
                Error = $"Ambiguous — {candidates.Count} symbols named '{name}'. " +
                        "Pass file (and line) of the declaration to disambiguate.\n" +
                        string.Join("\n", candidates.Select(c => $"  {c.Description} — {c.Path}")),
                Candidates = candidates,
            };
        }

        var symbol = declarations[0];

        // 2. Все ссылки по солюшену → замены по документам.
        //    Документ ищем по identity синтаксического дерева: Location.GetLineSpan().Path
        //    null для in-memory документов (тесты), а tree — есть всегда.
        var documentsByTree = new Dictionary<SyntaxTree, DocumentId>();
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                var tree = await document.GetSyntaxTreeAsync(ct);
                if (tree is not null && !documentsByTree.ContainsKey(tree))
                {
                    documentsByTree[tree] = document.Id;
                }
            }
        }

        var byDocument = new Dictionary<DocumentId, List<(int Position, int Length)>>();
        var affectedProjects = new HashSet<ProjectId>();

        // Декларация сама по себе: FindReferencesAsync в этой версии Roslyn возвращает
        // только ИСПОЛЬЗОВАНИЯ, декларацию в locations нет (проверено пробником).
        // Без этого rename переименовал бы вызовы, но не сам член → сломанный код.
        foreach (var declaration in symbol.Locations.Where(l => l.IsInSource))
        {
            var tree = declaration.SourceTree;
            if (tree is null || !documentsByTree.TryGetValue(tree, out var documentId))
            {
                continue;
            }
            // Span декларации начинается с модификатора (public ...): ищем именно
            // токен имени внутри узла декларации.
            var root = await tree.GetRootAsync(ct);
            var node = root.FindNode(declaration.SourceSpan);
            var namePosition = FindDeclarationNamePosition(node, name);
            if (namePosition is null)
            {
                continue;
            }
            AddReplacement(byDocument, solution, documentId, namePosition.Value, name.Length, affectedProjects);
        }

        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, ct))
        {
            foreach (var referenceLocation in reference.Locations)
            {
                var location = referenceLocation.Location;
                if (!location.IsInSource)
                {
                    continue;
                }
                var tree = location.SourceTree;
                if (tree is null || !documentsByTree.TryGetValue(tree, out var documentId))
                {
                    continue;
                }
                if (!byDocument.TryGetValue(documentId, out var list))
                {
                    list = new List<(int, int)>();
                    byDocument[documentId] = list;
                }
                AddReplacement(byDocument, solution, documentId, location.SourceSpan.Start, name.Length, affectedProjects);
            }
        }
        if (byDocument.Count == 0)
        {
            return new Outcome { Error = $"no references to '{name}' found" };
        }

        var totalLocations = byDocument.Values.Sum(l => l.Count);

        // 3. Пробный rename в памяти.
        var modified = solution;
        var changedDocuments = new List<(Document, string)>();
        foreach (var (documentId, replacements) in byDocument)
        {
            var document = modified.GetDocument(documentId)!;
            var text = await document.GetTextAsync(ct);
            var newText = text.ToString();
            // Замены с конца — позиции не сдвигаются.
            foreach (var (position, length) in replacements.OrderByDescending(r => r.Position))
            {
                // Проверка: именно имя (не префикс длинного слова).
                if (newText.Substring(position, length) != name)
                {
                    continue;
                }
                var next = position + length;
                if (next < newText.Length && (char.IsLetterOrDigit(newText[next]) || newText[next] == '_'))
                {
                    continue;
                }
                newText = newText[..position] + newName + newText[next..];
            }
            modified = modified.WithDocumentText(documentId, SourceText.From(newText));
            changedDocuments.Add((modified.GetDocument(documentId)!, newText));
        }

        // 4. Коллизии ловит компилятор: дифф ошибок до/после по затронутым проектам.
        var newErrors = await DiffErrorsAsync(solution, modified, affectedProjects, ct);
        if (newErrors.Count > 0)
        {
            return new Outcome
            {
                Error = $"Rename rejected: '{newName}' collides ({newErrors.Count} compiler error(s)). Nothing changed.\n" +
                        string.Join("\n", newErrors.Take(10).Select(e => "  " + e)),
            };
        }

        var changedFiles = byDocument
            .Select(kv => (
                Path: FormatPath(kv.Key, solution),
                Locations: kv.Value.Count))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        return new Outcome
        {
            Modified = modified,
            ChangedDocuments = changedDocuments,
            ChangedFiles = changedFiles,
            TotalLocations = totalLocations,
            SymbolDescription = Describe(symbol),
        };
    }

    /// <summary>
    /// Дедуп по позиции: FindReferencesAsync возвращает «семью» символа (у свойства:
    /// само свойство, авто-поле, геттеры — все с декларацией в одной позиции),
    /// иначе замена и счётчик задвоены.
    /// </summary>
    private static void AddReplacement(
        Dictionary<DocumentId, List<(int Position, int Length)>> byDocument,
        Solution solution, DocumentId documentId, int position, int length, HashSet<ProjectId> affectedProjects)
    {
        if (!byDocument.TryGetValue(documentId, out var list))
        {
            list = new List<(int, int)>();
            byDocument[documentId] = list;
        }
        if (list.Any(r => r.Position == position))
        {
            return;
        }
        list.Add((position, length));
        affectedProjects.Add(solution.GetDocument(documentId)!.Project.Id);
    }

    /// <summary>
    /// Позиция токена ИМЕНИ внутри узла декларации (span декларации начинается с
    /// модификатора, а имя — где-то внутри). Для поля — декларатор с нашим именем.
    /// </summary>
    private static int? FindDeclarationNamePosition(SyntaxNode? node, string name)
    {
        switch (node)
        {
            case TypeDeclarationSyntax type:
                return type.Identifier.SpanStart;
            case MethodDeclarationSyntax method:
                return method.Identifier.SpanStart;
            case PropertyDeclarationSyntax property:
                return property.Identifier.SpanStart;
            case EventDeclarationSyntax e:
                return e.Identifier.SpanStart;
            case FieldDeclarationSyntax field:
                // public int Bar, Baz; — декларатор с нашим именем
                return field.Declaration.Variables.FirstOrDefault(v => v.Identifier.Text == name)?.Identifier.SpanStart;
            default:
                return null;
        }
    }

    // ─────────────────────────── вспомогательное ───────────────────────────

    private static async Task<List<ISymbol>> FindDeclarationsAsync(Solution solution, string name, CancellationToken ct)
    {
        var declarations = new List<ISymbol>();
        var seen = new HashSet<string>();
        foreach (var project in solution.Projects)
        {
            foreach (var symbol in await SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: false, SymbolFilter.All, ct))
            {
                var declaration = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                if (declaration is null)
                {
                    continue;
                }
                var span = declaration.GetLineSpan();
                if (!seen.Add($"{span.Path}:{span.StartLinePosition}"))
                {
                    continue;
                }
                declarations.Add(symbol);
            }
        }
        return declarations;
    }

    private static bool MatchesLocation(ISymbol symbol, string? file, int? line)
    {
        var declaration = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (declaration is null)
        {
            return false;
        }
        var span = declaration.GetLineSpan();
        if (line is not null && span.StartLinePosition.Line + 1 != line)
        {
            return false;
        }
        if (file is not null)
        {
            var expected = Path.IsPathRooted(file)
                ? file
                : Path.Combine(SelfBuildPaths.WorkspaceRoot, file);
            // Живой солюшен: полный путь. In-memory (тесты): относительный путь документа.
            if (!string.Equals(span.Path, expected, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(span.Path, file, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static string Describe(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol m => $"method {m.ToDisplayString()}",
            IPropertySymbol p => $"property {p.ToDisplayString()}",
            IFieldSymbol f => $"field {f.ToDisplayString()}",
            IEventSymbol e => $"event {e.ToDisplayString()}",
            ITypeSymbol t => $"type {t.ToDisplayString()}",
            _ => symbol.ToDisplayString(),
        };
    }

    private static string FormatLocation(Location? location)
    {
        if (location is null || !location.IsInSource)
        {
            return "—";
        }
        var span = location.GetLineSpan();
        return $"{FormatPathRaw(span.Path)}:{span.StartLinePosition.Line + 1}";
    }

    private static string FormatPath(DocumentId documentId, Solution solution)
    {
        var path = solution.GetDocument(documentId)?.FilePath;
        return path is null ? "<in-memory>" : FormatPathRaw(path);
    }

    private static string FormatPathRaw(string? path)
    {
        if (path is null)
        {
            return "?";
        }
        try
        {
            return Path.GetRelativePath(SelfBuildPaths.WorkspaceRoot, path).Replace('\\', '/');
        }
        catch
        {
            return path;
        }
    }

    /// <summary>Дифф ошибок компиляции до/после rename по затронутым проектам.</summary>
    private static async Task<List<string>> DiffErrorsAsync(
        Solution before, Solution after, HashSet<ProjectId> projects, CancellationToken ct)
    {
        var beforeErrors = new HashSet<string>();
        var afterErrors = new List<string>();
        foreach (var projectId in projects)
        {
            var compilationBefore = await before.GetProject(projectId).GetCompilationAsync(ct);
            if (compilationBefore is not null)
            {
                foreach (var diagnostic in compilationBefore.GetDiagnostics(ct))
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        beforeErrors.Add(FormatDiagnostic(diagnostic));
                    }
                }
            }
            var compilationAfter = await after.GetProject(projectId).GetCompilationAsync(ct);
            if (compilationAfter is not null)
            {
                foreach (var diagnostic in compilationAfter.GetDiagnostics(ct))
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        afterErrors.Add(FormatDiagnostic(diagnostic));
                    }
                }
            }
        }
        return afterErrors.Where(e => !beforeErrors.Contains(e)).Distinct().ToList();
    }

    private static string FormatDiagnostic(Diagnostic diagnostic)
    {
        var location = diagnostic.Location.GetLineSpan();
        var path = location.Path is null ? "?" : FormatPathRaw(location.Path);
        return $"{diagnostic.Id}: {diagnostic.GetMessage()} ({path}:{location.StartLinePosition.Line + 1})";
    }
}
