using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// Пакетный репорт по счётчикам ссылок (диагностика здоровья кода) — VS CodeLens,
/// но одним вызовом вместо N, с фильтрами по вкусу:
/// - members-режим: счётчик ссылок на каждого члена типа (мёртвые API, одно-ссыльные);
/// - types-режим: счётчик ссылок на каждый тип namespace/солюшена («сложный класс,
///   а ссылок один»).
/// Счётчик = C#-ссылки (без декларации, как в VS CodeLens) + XAML-ссылки (биндинги,
/// теги элементов — для WPF-приложения это реальные ссылки; разбивка в note).
/// Шум свёрнут: аксессоры (get_/set_/add_/remove_) и бэкинг-поля не строчатся
/// отдельно — свойство/событие покрывает их. 0 ссылок = кандидат, не доказательство:
/// [Tool] помечается «via reflection», тест-типы — «test framework», делегатная
/// индирекция (f = x => M(x)) и строковые lookup'ы не видны — честный кавек в футере.
/// </summary>
[Tool("csharp_reference_report",
    "Batch reference-count report (code-health diagnostics), like VS CodeLens but in one call. " +
    "Modes: 'members' (default) — per-member reference counts of a type (find dead API with 0 refs, " +
    "single-use abstractions with 1 ref); 'types' — per-type reference counts for a namespace or the " +
    "whole solution (find heavy classes referenced by one element). " +
    "Refs = C# references (excluding the declaration, like VS CodeLens) + XAML references " +
    "(bindings and element tags — real references for a WPF app; breakdown in the note column). " +
    "Accessors and backing fields are folded into their property/event (not listed separately). " +
    "0 refs = candidate, not proof: [Tool] members are marked 'via reflection', test-project types " +
    "'test framework', delegate indirection (f = x => M(x)) and string-based lookups are not visible. " +
    "Filters: maxRefs (0 = dead only, 1 = dead + single-use), minRefs (hotspots), access, kinds, " +
    "file (types mode: only types declared in this file), limit. " +
    "deep (types mode, SLOW): computes per-type dead-member ratio for the rows shown. " +
    "saveDetail writes the full unlimited report to reports/reference_report_<type>.md and returns " +
    "summary + path (attach the file to a message to read it).", ToolGroup.CSharp)]
public sealed class CSharpReferenceReportTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    [ToolParameter("Type name to report on (required in 'members' mode)", Required = false)]
    public string? Type { get; set; }

    [ToolParameter("Report mode: 'members' (default) or 'types'", Required = false)]
    public string Mode { get; set; } = "members";

    [ToolParameter("Namespace for 'types' mode (default: all types of the solution)", Required = false)]
    public string? Namespace { get; set; }

    [ToolParameter("File filter for 'types' mode: only types declared in this file (relative to workspace root)", Required = false)]
    public string? File { get; set; }

    [ToolParameter("Access filter: 'all' (default, includes private) or 'public'", Required = false)]
    public string Access { get; set; } = "all";

    [ToolParameter("Member kinds: 'all' (default), 'methods', 'properties', 'fields', 'events'", Required = false)]
    public string Kinds { get; set; } = "all";

    [ToolParameter("Show only members with refs <= N (e.g. 0 = dead only, 1 = dead + single-use)", Required = false)]
    public int MaxRefs { get; set; } = -1;

    [ToolParameter("Show only members with refs >= N (hotspots)", Required = false)]
    public int MinRefs { get; set; } = -1;

    [ToolParameter("Max rows in the inline report (default 60; summary always shows the full picture)", Required = false)]
    public int Limit { get; set; } = 60;

    [ToolParameter("Write the full (unlimited) report to reports/reference_report_<type>.md, return summary + path", Required = false)]
    public bool SaveDetail { get; set; }

    [ToolParameter("'types' mode only, SLOW: compute per-type dead-member ratio (0-refs members / all members) for the rows shown", Required = false)]
    public bool Deep { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        // XAML-индекс строится ОДИН раз на прогон: имя → (сколько строк XAML под src/
        // его упоминают, первое совпадение file:line).
        var xamlIndex = await BuildXamlIndexAsync(cancellationToken);
        if (Mode.Trim().Equals("types", StringComparison.OrdinalIgnoreCase))
        {
            return await TypesModeAsync(solution, xamlIndex, cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(Type))
        {
            return "Error: 'type' is required in members mode.";
        }
        return await MembersModeAsync(solution, Type.Trim(), xamlIndex, cancellationToken);
    }

    // ─────────────────────────── members ───────────────────────────

    private async Task<string> MembersModeAsync(Solution solution, string typeName, IReadOnlyDictionary<string, XamlUse> xaml, CancellationToken ct)
    {
        var (type, error) = await FindTypeAsync(solution, typeName, ct);
        if (error is not null)
        {
            return error;
        }
        if (type is null)
        {
            return $"type '{typeName}' not found";
        }

        var rows = new List<ReportRow>();
        foreach (var member in type.GetMembers()
                      .Where(m => Access.Trim().Equals("public", StringComparison.OrdinalIgnoreCase)
                                  ? m.DeclaredAccessibility == Accessibility.Public
                                  : true)
                      .Where(m => MatchesKind(m, Kinds))
                      .Where(NotNoise))
        {
            var locations = await FindSourceLocationsAsync(member, solution, ct);
            var xamlUse = xaml.TryGetValue(member.Name, out var x) ? x : default;
            var row = new ReportRow
            {
                Name = member.Name,
                Kind = KindName(member),
                // C#-ссылки (без декларации) + XAML-ссылки: для WPF биндинг — реальная ссылка.
                Refs = locations.Count + xamlUse.Count,
                FirstUse = locations.Count > 0
                    ? FormatLocation(locations.FirstOrDefault())
                    : (xamlUse.Count > 0 ? xamlUse.First : "—"),
                Note = BuildNote(member),
            };
            if (xamlUse.Count > 0)
            {
                row.Note = AppendNote(row.Note, $"xaml: {xamlUse.Count} ({xamlUse.First})");
            }
            rows.Add(row);
        }

        return FormatReport(
            $"{typeName} — {rows.Count} members (access={Access}, kinds={Kinds}, accessors/backing fields folded)",
            rows,
            typeName,
            SaveDetail);
    }

    // ─────────────────────────── types ───────────────────────────

    private async Task<string> TypesModeAsync(Solution solution, IReadOnlyDictionary<string, XamlUse> xaml, CancellationToken ct)
    {
        var rows = new List<ReportRow>();
        var typeByKey = new Dictionary<string, ITypeSymbol>();
        foreach (var project in solution.Projects)
        {
            var allTypes = await CollectTypesAsync(project, ct);
            var types = allTypes
                .Where(t => Namespace is null || t.ContainingNamespace.ToString() == Namespace)
                .Where(t => File is null || SameFile(t, File))
                .ToList();
            foreach (var type in types)
            {
                var locations = await FindSourceLocationsAsync(type, solution, ct);
                var xamlUse = xaml.TryGetValue(type.Name, out var x) ? x : default;
                var row = new ReportRow
                {
                    Name = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Kind = "type",
                    Refs = locations.Count + xamlUse.Count,
                    FirstUse = locations.Count > 0
                        ? FormatLocation(locations.FirstOrDefault())
                        : (xamlUse.Count > 0 ? xamlUse.First : "—"),
                    Note = BuildNote(type),
                };
                if (xamlUse.Count > 0)
                {
                    row.Note = AppendNote(row.Note, $"xaml: {xamlUse.Count} ({xamlUse.First})");
                }
                rows.Add(row);
                typeByKey[row.Name] = type;
            }
        }

        // Deep (SLOW): для показанных строк — доля мёртвых членов у типа.
        if (Deep)
        {
            var filteredNames = rows
                .Where(r => (MaxRefs < 0 || r.Refs <= MaxRefs) && (MinRefs < 0 || r.Refs >= MinRefs))
                .Select(r => r.Name)
                .ToList();
            foreach (var name in filteredNames)
            {
                if (!typeByKey.TryGetValue(name, out var type))
                {
                    continue;
                }
                var (dead, total) = await CountDeadMembersAsync(type, solution, xaml, ct);
                var row = rows.First(r => r.Name == name);
                row.Note = AppendNote(row.Note, $"dead members: {dead}/{total}");
            }
        }

        return FormatReport(
            $"types — {rows.Count} types (namespace={Namespace ?? "<all>"}{(File is null ? "" : $", file={File}")})",
            rows,
            Namespace ?? "solution",
            SaveDetail);
    }

    /// <summary>Доля мёртвых членов у типа (0 C# + 0 XAML) — «rot ratio».</summary>
    private static async Task<(int Dead, int Total)> CountDeadMembersAsync(
        ITypeSymbol type, Solution solution, IReadOnlyDictionary<string, XamlUse> xaml, CancellationToken ct)
    {
        var total = 0;
        var dead = 0;
        foreach (var member in type.GetMembers().Where(m => m is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol).Where(NotNoise))
        {
            total++;
            var locations = await FindSourceLocationsAsync(member, solution, ct);
            var xamlCount = xaml.TryGetValue(member.Name, out var x) ? x.Count : 0;
            if (locations.Count + xamlCount == 0)
            {
                dead++;
            }
        }
        return (dead, total);
    }

    private static bool SameFile(ITypeSymbol type, string file)
    {
        var declaration = type.Locations.FirstOrDefault(l => l.IsInSource);
        if (declaration is null)
        {
            return false;
        }
        var path = declaration.GetLineSpan().Path;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        var expected = Path.IsPathRooted(file)
            ? file
            : Path.Combine(SelfBuildPaths.WorkspaceRoot, file);
        // Сепараторы: file-параметр приходит со слешами, span.Path — с бэкслашами.
        return string.Equals(
            (path ?? "").Replace('\\', '/'),
            expected.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Сбор всех типов проекта через синтаксис (TypeDeclarationSyntax покрывает
    /// class/struct/interface/enum/record, вложенные — включены обходом дерева).
    /// Синтаксический путь стабилен между версиями Roslyn, в отличие от
    /// расширений по обходу symbol-дерева.
    /// </summary>
    private static async Task<List<ITypeSymbol>> CollectTypesAsync(Project project, CancellationToken ct)
    {
        var types = new List<ITypeSymbol>();
        foreach (var document in project.Documents)
        {
            var root = await document.GetSyntaxRootAsync(ct);
            if (root is null)
            {
                continue;
            }
            var model = await document.GetSemanticModelAsync(ct);
            foreach (var node in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(node, ct) is ITypeSymbol type)
                {
                    types.Add(type);
                }
            }
        }
        return types;
    }

    // ─────────────────────────── XAML-индекс ───────────────────────────

    private readonly record struct XamlUse(int Count, string First);

    /// <summary>
    /// Один проход по *.xaml под src/ (run/ не трогаем): каждое идентификатор-
    /// совпадение индексируется → имя → (число строк, первое file:line). Биндинги
    /// ({Binding X}), теги элементов (&lt;local:X/>) и x:Class — всё это реальные
    /// ссылки WPF, невидимые Roslyn. Шум: значения атрибутов-слов («Top», «Center»)
    /// тоже считаются — кавек в футере.
    /// </summary>
    private static async Task<Dictionary<string, XamlUse>> BuildXamlIndexAsync(CancellationToken ct)
    {
        var index = new Dictionary<string, XamlUse>(StringComparer.Ordinal);
        var root = SelfBuildPaths.WorkspaceRoot;
        var identifier = new Regex(@"\b[A-Za-z_][A-Za-z0-9_]*\b");
        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!relative.StartsWith("src/", StringComparison.Ordinal))
            {
                continue;
            }
            // System.IO.File явно: File резолвится в свойство тула (в статике — CS0120).
            var text = await System.IO.File.ReadAllTextAsync(file, ct);
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in identifier.Matches(lines[i]))
                {
                    var name = match.Value;
                    if (!index.TryGetValue(name, out var entry))
                    {
                        entry = new XamlUse(0, $"{relative}:{i + 1}");
                    }
                    index[name] = new XamlUse(entry.Count + 1, entry.First);
                }
            }
        }
        return index;
    }

    // ─────────────────────────── общее ───────────────────────────

    /// <summary>
    /// Шум, который не несёт информации: аксессоры (свойство/событие их покрывает)
    /// и бэкинг-поля (компиляторные).
    /// </summary>
    private static bool NotNoise(ISymbol member)
    {
        if (member is IMethodSymbol method)
        {
            return method.MethodKind is not (MethodKind.PropertyGet or MethodKind.PropertySet
                                              or MethodKind.EventAdd or MethodKind.EventRemove);
        }
        if (member is IFieldSymbol field)
        {
            return !field.IsImplicitlyDeclared;
        }
        return true;
    }

    private static bool MatchesKind(ISymbol member, string kinds)
    {
        return kinds.Trim().ToLowerInvariant() switch
        {
            "all" => member is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol,
            "methods" => member is IMethodSymbol,
            "properties" => member is IPropertySymbol,
            "fields" => member is IFieldSymbol,
            "events" => member is IEventSymbol,
            _ => true,
        };
    }

    private static async Task<(ITypeSymbol? Type, string? Error)> FindTypeAsync(Solution solution, string typeName, CancellationToken ct)
    {
        var candidates = new List<ITypeSymbol>();
        var seenDeclarations = new HashSet<string>();
        foreach (var project in solution.Projects)
        {
            foreach (var symbol in await SymbolFinder.FindDeclarationsAsync(
                         project, typeName, ignoreCase: false, SymbolFilter.Type, ct))
            {
                if (symbol is not ITypeSymbol type)
                {
                    continue;
                }
                // FindDeclarationsAsync возвращает тип и как metadata-копию из каждого
                // проекта, ссылающегося на его проект: оставляем только исходные
                // декларации и дедуплицируем по месту декларации.
                var declaration = type.Locations.FirstOrDefault(l => l.IsInSource);
                if (declaration is null)
                {
                    continue;
                }
                var span = declaration.GetLineSpan();
                var key = $"{span.Path}:{span.StartLinePosition}";
                if (!seenDeclarations.Add(key))
                {
                    continue;
                }
                candidates.Add(type);
            }
        }
        if (candidates.Count == 0)
        {
            return (null, null);
        }
        if (candidates.Count > 1)
        {
            var list = string.Join("\n", candidates.Select(t =>
                $"  {t.ToDisplayString()} — {FormatLocation(t.Locations.FirstOrDefault(l => l.IsInSource))}"));
            return (null, $"Ambiguous — {candidates.Count} types named '{typeName}':\n{list}");
        }
        return (candidates[0], null);
    }

    private static async Task<List<Location>> FindSourceLocationsAsync(ISymbol symbol, Solution solution, CancellationToken ct)
    {
        var locations = new List<Location>();
        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, ct))
        {
            foreach (var referenceLocation in reference.Locations)
            {
                if (referenceLocation.Location.IsInSource)
                {
                    locations.Add(referenceLocation.Location);
                }
            }
        }
        return locations;
    }

    private static string FormatLocation(Location? location)
    {
        if (location is null || !location.IsInSource)
        {
            return "—";
        }
        var span = location.GetLineSpan();
        var path = Path.GetRelativePath(SelfBuildPaths.WorkspaceRoot, span.Path ?? "?");
        return $"{path.Replace('\\', '/')}:{span.StartLinePosition.Line + 1}";
    }

    private static string KindName(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor } => "ctor",
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            _ => "?",
        };
    }

    /// <summary>
    /// Метки «жив, но без C#-ссылок»: [Tool]-атрибут (ToolRegistry сканирует такие
    /// члены GetTypes()), тест-типы (xUnit находит их рефлексией).
    /// </summary>
    private static string? BuildNote(ISymbol symbol)
    {
        var notes = new List<string>();
        if (symbol.GetAttributes().Any(a => a.AttributeClass?.Name is "ToolAttribute" or "Tool"))
        {
            notes.Add("via reflection ([Tool])");
        }
        if (symbol is ITypeSymbol type &&
            (type.ContainingAssembly.Name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
             type.ContainingAssembly.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)))
        {
            notes.Add("test framework (reflection)");
        }
        return notes.Count > 0 ? string.Join("; ", notes) : null;
    }

    private static string AppendNote(string? existing, string note)
    {
        return existing is null ? note : $"{existing}; {note}";
    }

    private static string Summary(List<ReportRow> rows)
    {
        int zero = rows.Count(r => r.Refs == 0);
        int one = rows.Count(r => r.Refs == 1);
        int few = rows.Count(r => r.Refs is >= 2 and <= 5);
        int many = rows.Count(r => r.Refs > 5);
        var ratio = rows.Count > 0 ? zero * 100.0 / rows.Count : 0;
        return $"summary: 0 refs: {zero} · 1 ref: {one} · 2–5: {few} · 6+: {many} · dead ratio: {ratio:0}%";
    }

    private const string Caveats =
        "caveats: C# count excludes the declaration (like VS CodeLens); XAML count = identifier " +
        "occurrences in *.xaml under src/ (bindings, element tags, x:Class — plus attribute-word noise " +
        "like 'Top'/'Center'); 0 refs = candidate, not proof —\n" +
        "[Tool] members are marked 'via reflection', test-project types 'test framework', delegate " +
        "indirection (f = x => M(x)) and string-based lookups are not visible.";

    private string FormatReport(string title, List<ReportRow> allRows, string reportName, bool saveDetail)
    {
        var filtered = allRows
            .Where(r => MaxRefs < 0 || r.Refs <= MaxRefs)
            .Where(r => MinRefs < 0 || r.Refs >= MinRefs)
            .OrderBy(r => r.Refs)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine($"reference report: {title}");
        builder.AppendLine($"refs  {Pad("member", 24)} {Pad("kind", 9)} first use");
        var limit = Math.Max(1, Limit);
        foreach (var row in filtered.Take(limit))
        {
            builder.AppendLine($"{row.Refs,4}  {Pad(row.Name, 24)} {Pad(row.Kind, 9)} {row.FirstUse}{NoteSuffix(row.Note)}");
        }
        if (filtered.Count > limit)
        {
            builder.AppendLine($"(… {filtered.Count - limit} more rows — raise 'limit' or use 'saveDetail')");
        }
        builder.AppendLine();
        builder.AppendLine(Summary(filtered));
        builder.AppendLine(Caveats);

        if (!saveDetail)
        {
            return builder.ToString().TrimEnd();
        }

        // Детальный отчёт: ВСЕ строки (без фильтров и лимита) → файл, в ответ — сводка.
        var detail = new StringBuilder();
        detail.AppendLine($"# reference report: {title}");
        detail.AppendLine();
        detail.AppendLine("| refs | member | kind | first use | note |");
        detail.AppendLine("|---:|---|---|---|---|");
        foreach (var row in allRows.OrderBy(r => r.Refs).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            detail.AppendLine($"| {row.Refs} | {row.Name} | {row.Kind} | {row.FirstUse} | {row.Note ?? ""} |");
        }
        detail.AppendLine();
        detail.AppendLine(Summary(allRows));
        detail.AppendLine();
        detail.AppendLine(Caveats.Replace('\n', ' '));

        var directory = Path.Combine(SelfBuildPaths.WorkspaceRoot, "reports");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"reference_report_{Sanitize(reportName)}.md");
        // System.IO.File явно: File без префикса резолвится в свойство тула.
        System.IO.File.WriteAllText(file, detail.ToString());
        var relative = Path.GetRelativePath(SelfBuildPaths.WorkspaceRoot, file).Replace('\\', '/');

        builder.AppendLine();
        builder.AppendLine($"full report: {relative} ({allRows.Count} rows) — attach the file to read it");
        return builder.ToString().TrimEnd();
    }

    private static string NoteSuffix(string? note)
    {
        return string.IsNullOrEmpty(note) ? "" : $"  [{note}]";
    }

    private static string Pad(string value, int width) =>
        value.Length >= width ? value : value.PadRight(width);

    private static string Sanitize(string name)
    {
        return new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    }

    private sealed class ReportRow
    {
        public string Name { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public int Refs { get; init; }
        public string FirstUse { get; init; } = "—";
        public string? Note { get; set; }
    }
}
