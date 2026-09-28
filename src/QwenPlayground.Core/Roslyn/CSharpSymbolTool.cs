using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

[Tool("csharp_symbol", "Find C# symbol declarations by name in the QwenPlayground solution. " +
    "Returns kind, signature and file:line — for a multi-line declaration the whole line range " +
    "(file:12-45) plus the first line of the comment above it, so you know what the element is " +
    "and where it ends.", ToolGroup.CSharp)]
public sealed class CSharpSymbolTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    private const int MaxResults = 50;

    [ToolParameter("Symbol name to find, e.g. QwenChatTemplate or Render", Required = true)]
    public string Name { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<string>();

        foreach (var project in solution.Projects)
        {
            // Один и тот же символ находится столько раз, сколько проектов ссылается на
            // его проект: без дедупа по месту декларации выдача из семи одинаковых строк.
            var symbols = await SymbolFinder.FindDeclarationsAsync(project, Name, ignoreCase: false,
                SymbolFilter.All, cancellationToken);
            foreach (var symbol in symbols)
            {
                if (!symbol.Locations.Any(l => l.IsInSource))
                {
                    continue;
                }
                var element = await LocationFormatter.ElementAsync(symbol, cancellationToken);
                if (!seen.Add(element.Reference))
                {
                    continue;
                }
                results.Add(LocationFormatter.Declaration(element, symbol));
                if (results.Count >= MaxResults)
                {
                    return string.Join('\n', results) + $"\n... (truncated at {MaxResults})";
                }
            }
        }

        return results.Count > 0 ? string.Join('\n', results) : $"no symbol named '{Name}' found";
    }
}
