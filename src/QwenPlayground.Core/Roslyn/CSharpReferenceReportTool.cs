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
/// но одним вызовом вместо N:
/// - members-режим: счётчик ссылок на каждого члена типа (мёртвые API, одно-ссыльные);
/// - types-режим: счётчик ссылок на каждый тип namespace/солюшена («сложный класс,
///   а ссылок один»).
/// Счётчик исключает саму декларацию. 0 ссылок — кандидат, не доказательство:
/// XAML-биндинги кросс-чекаются (метка [XAML:]), [Tool]-члены помечаются «жив через
/// рефлексию», делегатная индирекция (f = x => M(x)) не видна — честный кавек в футере.
/// </summary>
[Tool("csharp_reference_report",
    "Batch reference-count report (code-health diagnostics), like VS CodeLens but in one call. " +
    "Modes: 'members' (default) — per-member reference counts of a type (find dead API with 0 refs, " +
    "single-use abstractions with 1 ref); 'types' — per-type reference counts for a namespace or the " +
    "whole solution (find heavy classes referenced by one element). Count excludes the declaration. " +
    "0 refs = candidate, not proof: XAML bindings are cross-checked and marked [XAML:], [Tool] members " +
    "are marked 'via reflection', delegate indirection (f = x => M(x)) is not visible. " +
    "Use maxRefs:0 for dead-only, maxRefs:1 for dead+near-dead, minRefs:N for hotspots. " +
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

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        if (Mode.Trim().Equals("types", StringComparison.OrdinalIgnoreCase))
        {
            return await TypesModeAsync(solution, cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(Type))
        {
            return "Error: 'type' is required in members mode.";
        }
        return await MembersModeAsync(solution, Type.Trim(), cancellationToken);
    }

    // ─────────────────────────── members ───────────────────────────

    private async Task<string> MembersModeAsync(Solution solution, string typeName, CancellationToken ct)
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
                     .Where(m => MatchesKind(m, Kinds)))
        {
            var locations = await FindSourceLocationsAsync(member, solution, ct);
            var declaration = member.Locations.FirstOrDefault(l => l.IsInSource);
            var firstUse = locations.FirstOrDefault(l => !IsSameLocation(l, declaration));
            var row = new ReportRow
            {
                Name = member.Name,
                Kind = KindName(member),
                Refs = Math.Max(0, locations.Count - 1), // декларацию не считаем
                FirstUse = FormatLocation(firstUse),
                Note = BuildNote(member),
            };
            // XAML-кроссчек для нулей: WPF-биндинг не виден Roslyn как C#-ссылка.
            if (row.Refs == 0)
            {
                var xaml = await FindXamlUseAsync(row.Name, ct);
                if (xaml is not null)
                {
                    row.Note = string.Join("; ", new[] { row.Note, $"[XAML: {xaml}]" }.Where(s => s is not null));
                }
            }
            rows.Add(row);
        }

        return FormatReport(
            $"{typeName} — {rows.Count} members (access={Access}, kinds={Kinds})",
            rows,
            typeName,
            SaveDetail);
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

    // ─────────────────────────── types ───────────────────────────

    private async Task<string> TypesModeAsync(Solution solution, CancellationToken ct)
    {
        var rows = new List<ReportRow>();
        foreach (var project in solution.Projects)
        {
            var allTypes = await CollectTypesAsync(project, ct);
            var types = allTypes
                .Where(t => Namespace is null || t.ContainingNamespace.ToString() == Namespace)
                .ToList();
            foreach (var type in types)
            {
                var locations = await FindSourceLocationsAsync(type, solution, ct);
                var declaration = type.Locations.FirstOrDefault(l => l.IsInSource);
                var firstUse = locations.FirstOrDefault(l => !IsSameLocation(l, declaration));
                rows.Add(new ReportRow
                {
                    Name = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Kind = "type",
                    Refs = Math.Max(0, locations.Count - 1),
                    FirstUse = FormatLocation(firstUse),
                    Note = BuildNote(type),
                });
            }
        }

        return FormatReport(
            $"types — {rows.Count} types (namespace={Namespace ?? "<all>"})",
            rows,
            Namespace ?? "solution",
            SaveDetail);
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

    // ─────────────────────────── общее ───────────────────────────

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

    private static bool IsSameLocation(Location? a, Location? b)
    {
        if (a is null || b is null || !a.IsInSource || !b.IsInSource)
        {
            return a is null && b is null;
        }
        var spanA = a.GetLineSpan();
        var spanB = b.GetLineSpan();
        return spanA.Path == spanB.Path
            && spanA.StartLinePosition == spanB.StartLinePosition
            && spanA.EndLinePosition == spanB.EndLinePosition;
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
    /// Метка «жив через рефлексию»: [Tool]-атрибут — ToolRegistry сканирует такие
    /// члены GetTypes() и не оставляет им C#-ссылок.
    /// </summary>
    private static string? BuildNote(ISymbol member)
    {
        if (member.GetAttributes().Any(a => a.AttributeClass?.Name is "ToolAttribute" or "Tool"))
        {
            return "via reflection ([Tool])";
        }
        return null;
    }

    /// <summary>
    /// XAML-кроссчек: ищет имя члена в *.xaml под src/ (биндинги, x:Name, Tag).
    /// run/ (развёрнутые копии) не трогаем. Возвращает первое совпадение file:line.
    /// </summary>
    private static async Task<string?> FindXamlUseAsync(string name, CancellationToken ct)
    {
        var root = SelfBuildPaths.WorkspaceRoot;
        var pattern = new Regex($@"\b{Regex.Escape(name)}\b");
        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (!relative.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var text = await File.ReadAllTextAsync(file, ct);
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    return $"{relative.Replace('\\', '/')}:{i + 1}";
                }
            }
        }
        return null;
    }

    private static string Summary(List<ReportRow> rows)
    {
        int zero = rows.Count(r => r.Refs == 0);
        int one = rows.Count(r => r.Refs == 1);
        int few = rows.Count(r => r.Refs is >= 2 and <= 5);
        int many = rows.Count(r => r.Refs > 5);
        return $"summary: 0 refs: {zero} · 1 ref: {one} · 2–5: {few} · 6+: {many}";
    }

    private const string Caveats =
        "caveats: count excludes the declaration; 0 refs = candidate, not proof —\n" +
        "XAML bindings are cross-checked ([XAML:] mark), [Tool] members are marked 'via reflection',\n" +
        "delegate indirection (f = x => M(x)) and string-based lookups are not visible.";

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
            builder.AppendLine($"{row.Refs,4}  {Pad(row.Name, 24)} {Pad(row.Kind, 9)} {row.FirstUse}");
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
        File.WriteAllText(file, detail.ToString());
        var relative = Path.GetRelativePath(SelfBuildPaths.WorkspaceRoot, file).Replace('\\', '/');

        builder.AppendLine();
        builder.AppendLine($"full report: {relative} ({allRows.Count} rows) — attach the file to read it");
        return builder.ToString().TrimEnd();
    }

    private static string Pad(string value, int width) =>
        value.Length >= width ? value : value.PadRight(width);

    private static string Sanitize(string name) =>
        new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private sealed class ReportRow
    {
        public string Name { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public int Refs { get; init; }
        public string FirstUse { get; init; } = "—";
        public string? Note { get; set; }
    }
}
