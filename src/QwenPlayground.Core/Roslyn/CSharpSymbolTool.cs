using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

[Tool("csharp_symbol", "Find C# symbol declarations by name in the QwenPlayground solution. Returns kind, signature and file:line.")]
public sealed class CSharpSymbolTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    [ToolParameter("Symbol name to find, e.g. QwenChatTemplate or Render", Required = true)]
    public string Name { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        var results = new List<string>();

        foreach (var project in solution.Projects)
        {
            var symbols = await SymbolFinder.FindDeclarationsAsync(project, Name, ignoreCase: false,
                SymbolFilter.All, cancellationToken);
            foreach (var symbol in symbols)
            {
                foreach (var location in symbol.Locations)
                {
                    if (!location.IsInSource)
                    {
                        continue;
                    }
                    var position = location.GetLineSpan();
                    var path = Path.GetRelativePath(SelfBuild.SelfBuildPaths.WorkspaceRoot, position.Path ?? "?");
                    results.Add($"{symbol.Kind.ToString().ToLowerInvariant()} {symbol.ToDisplayString()} " +
                                $"— {path}:{position.StartLinePosition.Line + 1}");
                }
                if (results.Count >= 50)
                {
                    break;
                }
            }
            if (results.Count >= 50)
            {
                break;
            }
        }

        return results.Count > 0 ? string.Join('\n', results) : $"no symbol named '{Name}' found";
    }
}
