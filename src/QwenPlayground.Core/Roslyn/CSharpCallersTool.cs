using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// Вызывающие, сгруппированные по декларации вызываемого метода: заголовок группы —
/// сама декларация (диапазон строк + комментарий), строки ниже — вызывающие с
/// file:line и описанием. Тело метода по одной строке начала не угадать, а понять,
/// кто именно его зовёт, по одному имени — нельзя (одноимённых методов бывает много).
/// </summary>
[Tool("csharp_callers",
    "Find all callers of a C# method (call hierarchy) by method name in the QwenPlayground solution, " +
    "grouped by the declaration they call. Group header: the called declaration (kind, signature, " +
    "file:12-45 and the comment above it); each line: the calling symbol with its file:line, " +
    "its own comment and '(indirect)' for indirect callers. Use for impact analysis before refactoring.",
    ToolGroup.CSharp)]
public sealed class CSharpCallersTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    private const int MaxCallers = 50;
    private const int MaxMethods = 20;

    [ToolParameter("Method name to find callers for, e.g. Render", Required = true)]
    public string Name { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<string>();
        var total = 0;
        var done = false;

        foreach (var project in solution.Projects)
        {
            if (done)
            {
                break;
            }
            // SymbolFilter в Roslyn 5.x упрощён (Type/Member/All); из членов берём только методы.
            var methods = (await SymbolFinder.FindDeclarationsAsync(project, Name, ignoreCase: false, SymbolFilter.Member, cancellationToken))
                .OfType<IMethodSymbol>();
            foreach (var method in methods)
            {
                if (done)
                {
                    break;
                }
                // Метод находится столько раз, сколько проектов ссылается на его проект —
                // без дедупа по месту декларации группа (и её вызывающие) повторяется.
                var element = await LocationFormatter.ElementAsync(method, cancellationToken);
                if (element.File is null || !seen.Add(element.Reference))
                {
                    continue;
                }
                var lines = new List<string>();
                var callers = await SymbolFinder.FindCallersAsync(method, solution, cancellationToken);
                foreach (var caller in callers)
                {
                    var where = LocationFormatter.Usage(caller.Locations.FirstOrDefault(l => l.IsInSource))
                                ?? "(no source location)";
                    var suffix = caller.IsDirect ? string.Empty : " (indirect)";
                    var doc = await LocationFormatter.DocAsync(caller.CallingSymbol, cancellationToken);
                    lines.Add(LocationFormatter.WithDoc(
                        $"  {caller.CallingSymbol.ToDisplayString()} — {where}{suffix}", doc));
                    total++;
                    if (total >= MaxCallers || groups.Count >= MaxMethods)
                    {
                        done = true;
                        break;
                    }
                }
                if (lines.Count > 0)
                {
                    groups.Add(LocationFormatter.Declaration(element, method) + "\n" + string.Join('\n', lines));
                }
            }
        }

        if (groups.Count == 0)
        {
            return $"no callers of '{Name}' found";
        }
        var builder = new StringBuilder();
        builder.Append(total).Append(" caller(s) of '").Append(Name).Append("' in ")
            .Append(groups.Count).Append(" declaration(s):\n");
        builder.Append(string.Join('\n', groups));
        if (done)
        {
            builder.Append($"\n... (truncated at {MaxCallers} callers / {MaxMethods} declarations)");
        }
        return builder.ToString();
    }
}
