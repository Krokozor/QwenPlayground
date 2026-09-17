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
/// Перенос типа в другой файл/namespace. Ядро — чистая функция Solution →
/// (Solution, отчёт) (internal, тестируется на in-memory workspace); тул — тонкая
/// обёртка: Shared-солюшен → ядро → запись файлов → применение к живому workspace.
/// Безопасность — та же, что у csharp_rename: пробный результат компилируется до
/// сохранения, новые ошибки → отмена. v1-ограничения: только top-level типы
/// (не вложенные), не partial (все части сразу), usings переносятся консервативно
/// (все из исходного файла), квалификаторы OldNs.Type переписываются текстом.
/// </summary>
[Tool("csharp_move_type",
    "Move a C# top-level type to another file and/or namespace. The type declaration is " +
    "extracted from its current file, written to the target file (inside the target " +
    "namespace), all usings from the source file are carried over, references are updated " +
    "(qualified 'OldNs.Type' rewritten to 'NewNs.Type'; a 'using' is added to files that " +
    "refer to the type unqualified). Before saving, the result is compiled — if the move " +
    "breaks anything (e.g. a same-named type in the target namespace), it is rejected and " +
    "nothing is changed. Limitations: top-level types only (not nested), not partial " +
    "classes (move all parts together), cosmetic formatting is not preserved (run an " +
    "formatter afterwards if it matters).", ToolGroup.CSharp)]
public sealed class CSharpMoveTypeTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    [ToolParameter("Type name to move", Required = true)]
    public string Name { get; set; } = string.Empty;

    [ToolParameter("File of the declaration (relative to workspace root), disambiguates same-named types", Required = false)]
    public string? File { get; set; }

    [ToolParameter("Target file path (relative to workspace root)", Required = true)]
    public string NewFile { get; set; } = string.Empty;

    [ToolParameter("Target namespace (omit to keep the current one)", Required = false)]
    public string? NewNamespace { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        var outcome = await CSharpMoveTypeCore.ApplyAsync(solution, Name, File, NewFile, NewNamespace, cancellationToken);
        if (outcome.Error is not null)
        {
            return outcome.Error;
        }

        // 1. Диск (до ApplyChanges: _loadedAt=Now покрывает новые write-time файлов).
        foreach (var (path, text) in outcome.FileWrites)
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }
            System.IO.File.WriteAllText(path, text);
        }
        // 2. Живой workspace.
        await Service.ApplyChangesAsync(outcome.Modified!, cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine($"moved '{Name}' ({outcome.OldNamespace ?? "<global>"}) → {outcome.NewNamespace ?? "<global>"} / {outcome.NewFile}");
        builder.AppendLine($"  new file: {outcome.NewFile}");
        foreach (var (path, note) in outcome.ChangedFiles)
        {
            builder.AppendLine($"  modified: {path} ({note})");
        }
        builder.AppendLine($"  {outcome.TotalUsages} usage location(s) across {outcome.UsagesPerFile.Count} file(s)");
        builder.AppendLine("  diagnostics: clean (pre-save compile check passed)");
        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// Ядро move_type: чистая функция Solution → (Solution, отчёт). Тестируется на
/// in-memory AdhocWorkspace без прикосновения к живому солюшену.
/// </summary>
internal static class CSharpMoveTypeCore
{
    public sealed class Outcome
    {
        public string? Error { get; init; }
        public Solution? Modified { get; init; }
        public IReadOnlyList<(string Path, string Text)>? FileWrites { get; init; }
        public IReadOnlyList<(string Path, string Note)>? ChangedFiles { get; init; }
        public IReadOnlyDictionary<string, int>? UsagesPerFile { get; init; }
        public int TotalUsages { get; init; }
        public string? OldNamespace { get; init; }
        public string? NewNamespace { get; init; }
        public string? NewFile { get; init; }
    }

    public static async Task<Outcome> ApplyAsync(
        Solution solution, string typeName, string? file, string newFile, string? newNamespace, CancellationToken ct)
    {
        // 1. Декларация (дедуп по месту — как в rename).
        var declarations = await FindDeclarationsAsync(solution, typeName, ct);
        if (declarations.Count == 0)
        {
            return new Outcome { Error = $"type '{typeName}' not found" };
        }
        if (file is not null)
        {
            var match = declarations.FirstOrDefault(d => MatchesLocation(d, file));
            if (match is null)
            {
                return new Outcome { Error = $"type '{typeName}' not found in {file}" };
            }
            declarations = new List<ISymbol> { match };
        }
        if (declarations.Count > 1)
        {
            var candidates = declarations
                .Select(d => (
                    Description: d.ToDisplayString(),
                    Path: FormatLocation(d.Locations.FirstOrDefault(l => l.IsInSource))))
                .ToList();
            return new Outcome
            {
                Error = $"Ambiguous — {candidates.Count} types named '{typeName}'. " +
                        "Pass file of the declaration to disambiguate.\n" +
                        string.Join("\n", candidates.Select(c => $"  {c.Description} — {c.Path}")),
            };
        }

        var type = (ITypeSymbol)declarations[0];
        if (type is ITypeParameterSymbol)
        {
            return new Outcome { Error = "type parameter cannot be moved" };
        }
        if (type.ContainingType is not null)
        {
            return new Outcome { Error = $"'{typeName}' is nested in {type.ContainingType.Name} — v1 moves top-level types only" };
        }

        var declaration = type.Locations.FirstOrDefault(l => l.IsInSource);
        if (declaration is null || declaration.SourceTree is null)
        {
            return new Outcome { Error = "no source declaration found" };
        }

        // 2. Карта дерево → документ (Location.GetLineSpan().Path пуст для in-memory).
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
        if (!documentsByTree.TryGetValue(declaration.SourceTree!, out var oldDocumentId))
        {
            return new Outcome { Error = "declaration document not found" };
        }
        var oldDocument = solution.GetDocument(oldDocumentId)!;
        var oldRoot = await declaration.SourceTree!.GetRootAsync(ct);
        var typeNode = oldRoot.FindNode(declaration.SourceSpan) as TypeDeclarationSyntax;
        if (typeNode is null)
        {
            return new Outcome { Error = "declaration node is not a type declaration" };
        }
        // Partial по синтаксису (у ITypeSymbol в этой версии Roslyn нет IsPartial).
        if (typeNode.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            return new Outcome { Error = $"'{typeName}' is partial — v1 moves whole types; move all parts together" };
        }

        // ToString(), а не GetText(): у имени namespace в «namespace Ns1\n{» к
        // токену приклеен trailing trivia (перенос), GetText() его тащит.
        var oldNamespace = typeNode.Ancestors()
            .OfType<NamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString()
            ?? typeNode.Ancestors()
            .OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        var targetNamespace = string.IsNullOrWhiteSpace(newNamespace) ? oldNamespace : newNamespace;

        // 3. Цели: новый файл / тот же файл.
        var oldKey = DocKey(oldDocument);
        var isNewFile = !SameKey(oldKey, newFile);
        if (!isNewFile && targetNamespace == oldNamespace)
        {
            return new Outcome { Error = "nothing to do: same file and same namespace" };
        }
        if (isNewFile)
        {
            var existing = documentsByTree.Values
                .Select(solution.GetDocument)
                .FirstOrDefault(d => d is not null && SameKey(DocKey(d), newFile));
            if (existing is not null)
            {
                return new Outcome { Error = $"target file already exists in the solution: {newFile}" };
            }
        }

        // 4. Текст нового файла: usings исходного (консервативно, все) + namespace + тип.
        var usingLines = oldRoot.ChildNodes().OfType<UsingDirectiveSyntax>()
            .Select(u => u.ToString().Trim());
        var typeText = Indent(typeNode.WithoutLeadingTrivia().GetText().ToString().TrimEnd(), 4);
        var newFileText = BuildNewFileText(string.Join("\n", usingLines), targetNamespace, typeText);

        // 5. Старый файл: тип удалён.
        var newOldText = oldRoot.RemoveNode(typeNode, SyntaxRemoveOptions.KeepNoTrivia).GetText().ToString();

        // 6. Ссылки: использования (в этой версии Roslyn FindReferences их и возвращает).
        var usagesByDocument = new Dictionary<DocumentId, List<int>>();
        var affectedProjects = new HashSet<ProjectId> { oldDocument.Project.Id };
        foreach (var reference in await SymbolFinder.FindReferencesAsync(type, solution, ct))
        {
            foreach (var referenceLocation in reference.Locations)
            {
                var location = referenceLocation.Location;
                if (!location.IsInSource || location.SourceTree is null)
                {
                    continue;
                }
                if (!documentsByTree.TryGetValue(location.SourceTree, out var documentId))
                {
                    continue;
                }
                if (!usagesByDocument.TryGetValue(documentId, out var list))
                {
                    list = new List<int>();
                    usagesByDocument[documentId] = list;
                }
                list.Add(location.SourceSpan.Start);
                affectedProjects.Add(solution.GetDocument(documentId)!.Project.Id);
            }
        }

        // 7. Сборка финальных текстов.
        var fileWrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changedFiles = new List<(string, string)>();
        var usagesPerFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (isNewFile)
        {
            var newDocumentId = DocumentId.CreateNewId(oldDocument.Project.Id, newFile);
            fileWrites[ResolvePath(newFile)] = newFileText;
        }

        // Старый файл: удалённый тип + (если есть использования) using/квалификаторы.
        var oldFilePath = oldKey;
        var finalOldText = newOldText;
        var oldUsages = usagesByDocument.TryGetValue(oldDocumentId, out var ol) ? ol.Count : 0;
        if (oldUsages > 0)
        {
            finalOldText = UpdateReferences(finalOldText, oldNamespace, targetNamespace, typeName);
        }
        if (isNewFile || oldUsages > 0)
        {
            fileWrites[oldFilePath] = finalOldText;
            changedFiles.Add((oldFilePath, isNewFile ? "type removed" : "type removed; references updated"));
            if (oldUsages > 0)
            {
                usagesPerFile[oldFilePath] = oldUsages;
            }
        }

        // Прочие документы с использованиями.
        foreach (var (documentId, usagePositions) in usagesByDocument)
        {
            if (documentId == oldDocumentId)
            {
                continue;
            }
            var document = solution.GetDocument(documentId)!;
            var path = DocKey(document);
            var text = (await document.GetTextAsync(ct)).ToString();
            var updated = UpdateReferences(text, oldNamespace, targetNamespace, typeName);
            fileWrites[path] = updated;
            changedFiles.Add((path, oldNamespace is not null
                ? "qualified names rewritten"
                : $"+using {targetNamespace}"));
            usagesPerFile[path] = usagePositions.Count;
        }

        // 8. Пробный результат в памяти.
        var modified = solution;
        if (isNewFile)
        {
            modified = modified.AddDocument(
                DocumentId.CreateNewId(oldDocument.Project.Id, newFile), newFile, SourceText.From(newFileText));
        }
        modified = modified.WithDocumentText(oldDocumentId, SourceText.From(finalOldText));
        foreach (var (documentId, _) in usagesByDocument)
        {
            if (documentId == oldDocumentId)
            {
                continue;
            }
            var document = modified.GetDocument(documentId)!;
            modified = modified.WithDocumentText(documentId, SourceText.From(fileWrites[DocKey(document)]));
        }

        // 9. Коллизии/пробелы ловит компилятор: дифф ошибок до/после.
        var newErrors = await DiffErrorsAsync(solution, modified, affectedProjects, ct);
        if (newErrors.Count > 0)
        {
            return new Outcome
            {
                Error = $"Move rejected: {newErrors.Count} compiler error(s). Nothing changed.\n" +
                        string.Join("\n", newErrors.Take(10).Select(e => "  " + e)),
            };
        }

        return new Outcome
        {
            Modified = modified,
            FileWrites = fileWrites.Select(kv => (kv.Key, kv.Value)).ToList(),
            ChangedFiles = changedFiles,
            UsagesPerFile = usagesPerFile,
            TotalUsages = usagesPerFile.Values.Sum(),
            OldNamespace = oldNamespace,
            NewNamespace = targetNamespace,
            NewFile = newFile,
        };
    }

    // ─────────────────────────── вспомогательное ───────────────────────────

    private static string BuildNewFileText(string usings, string? targetNamespace, string typeText)
    {
        var builder = new StringBuilder();
        if (usings.Length > 0)
        {
            builder.AppendLine(usings);
            builder.AppendLine();
        }
        if (targetNamespace is null)
        {
            builder.Append(typeText);
        }
        else
        {
            builder.AppendLine($"namespace {targetNamespace}");
            builder.AppendLine("{");
            builder.Append(typeText);
            builder.AppendLine();
            builder.Append("}");
        }
        return builder.ToString();
    }

    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        return string.Join("\n", text.Split('\n').Select(l => l.Length > 0 ? pad + l : l));
    }

    /// <summary>
    /// Обновление ссылок в тексте документа: квалификаторы OldNs.Type → NewNs.Type
    /// (с проверкой границ) + using NewNs для неквалифицированных ссылок.
    /// </summary>
    private static string UpdateReferences(string text, string? oldNamespace, string? targetNamespace, string typeName)
    {
        if (oldNamespace is not null && targetNamespace is not null && oldNamespace != targetNamespace)
        {
            text = ReplaceQualified(text, $"{oldNamespace}.{typeName}", $"{targetNamespace}.{typeName}");
        }
        if (targetNamespace is null)
        {
            return text;
        }
        var usingDirective = $"using {targetNamespace};";
        var hasUsing = text.Contains(usingDirective, StringComparison.Ordinal);
        var inNamespace = text.Contains($"namespace {targetNamespace}", StringComparison.Ordinal);
        if (hasUsing || inNamespace)
        {
            return text;
        }
        // Вставка после последнего using (или в начало файла).
        var lastUsingEnd = -1;
        int index = 0;
        while ((index = text.IndexOf("using ", index, StringComparison.Ordinal)) >= 0)
        {
            var semi = text.IndexOf(';', index);
            if (semi < 0)
            {
                break;
            }
            lastUsingEnd = semi + 1;
            index = semi + 1;
        }
        if (lastUsingEnd < 0)
        {
            return usingDirective + "\n" + text;
        }
        var insertAt = lastUsingEnd;
        while (insertAt < text.Length && text[insertAt] != '\n')
        {
            insertAt++;
        }
        insertAt = Math.Min(insertAt + 1, text.Length);
        return text[..insertAt] + usingDirective + "\n" + text[insertAt..];
    }

    private static string ReplaceQualified(string text, string oldQualified, string newQualified)
    {
        var result = text;
        int index = 0;
        while ((index = result.IndexOf(oldQualified, index, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = index == 0
                || !(char.IsLetterOrDigit(result[index - 1]) || result[index - 1] == '_' || result[index - 1] == '.');
            var after = index + oldQualified.Length;
            var afterOk = after == result.Length
                || !(char.IsLetterOrDigit(result[after]) || result[after] == '_' || result[after] == '.');
            if (beforeOk && afterOk)
            {
                result = result[..index] + newQualified + result[after..];
                index = after + (newQualified.Length - oldQualified.Length);
            }
            else
            {
                index += oldQualified.Length;
            }
        }
        return result;
    }

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

    private static bool MatchesLocation(ISymbol symbol, string file)
    {
        var declaration = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (declaration is null)
        {
            return false;
        }
        var span = declaration.GetLineSpan();
        var expected = Path.IsPathRooted(file)
            ? file
            : Path.Combine(SelfBuildPaths.WorkspaceRoot, file);
        // Живой солюшен: полный путь. In-memory: имя/путь документа.
        return string.Equals(span.Path, expected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(span.Path, file, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Ключ документа: полный путь; для in-memory (тесты) — имя документа.
    /// </summary>
    private static string DocKey(Document document)
    {
        return string.IsNullOrEmpty(document.FilePath) ? document.Name : ResolvePath(document.FilePath);
    }

    private static bool SameKey(string docKey, string file)
    {
        return string.Equals(docKey, ResolvePath(file), StringComparison.OrdinalIgnoreCase)
            || string.Equals(docKey, file, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path) ? path : Path.Combine(SelfBuildPaths.WorkspaceRoot, path);
    }

    private static string FormatPathRaw(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "<in-memory>";
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
        var path = string.IsNullOrEmpty(location.Path) ? "?" : FormatPathRaw(location.Path);
        return $"{diagnostic.Id}: {diagnostic.GetMessage()} ({path}:{location.StartLinePosition.Line + 1})";
    }
}
