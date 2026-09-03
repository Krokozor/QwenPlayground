using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

[Tool("csharp_callers",
    "Find all callers of a C# method (call hierarchy) by method name in the QwenPlayground solution. " +
    "Returns each caller with its signature and file:line. Use for impact analysis before refactoring.", ToolGroup.CSharp)]
public sealed class CSharpCallersTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    private const int MaxCallers = 50;

    [ToolParameter("Method name to find callers for, e.g. Render", Required = true)]
    public string Name { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        var results = new List<string>();
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
                var callers = await SymbolFinder.FindCallersAsync(method, solution, cancellationToken);
                foreach (var caller in callers)
                {
                    var location = caller.Locations.FirstOrDefault(l => l.IsInSource);
                    var where = location is null
                        ? "(no source location)"
                        : $"{Path.GetRelativePath(SelfBuild.SelfBuildPaths.WorkspaceRoot, location.GetLineSpan().Path ?? "?")}:{location.GetLineSpan().StartLinePosition.Line + 1}";
                    var suffix = caller.IsDirect ? string.Empty : " (indirect)";
                    results.Add($"{caller.CallingSymbol.ToDisplayString()} — {where}{suffix}");
                    if (results.Count >= MaxCallers)
                    {
                        done = true;
                        break;
                    }
                }
            }
        }

        if (results.Count == 0)
        {
            return $"no callers of '{Name}' found";
        }
        var builder = new StringBuilder($"{results.Count} callers of '{Name}':\n");
        builder.Append(string.Join('\n', results));
        if (results.Count >= MaxCallers)
        {
            builder.Append("\n... (truncated at 50 callers)");
        }
        return builder.ToString();
    }
}
