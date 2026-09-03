using System.Text;
using Microsoft.CodeAnalysis;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

[Tool("csharp_diagnostics",
    "Get Roslyn compilation diagnostics (errors/warnings) for the QwenPlayground solution without building it.", ToolGroup.CSharp)]
public sealed class CSharpDiagnosticsTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    [ToolParameter("Minimum severity: error, warning or info")]
    public string Severity { get; set; } = "warning";

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var minimum = Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Error
            : Severity.Equals("info", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Info
            : DiagnosticSeverity.Warning;

        var solution = await Service.GetSolutionAsync(cancellationToken);
        var results = new List<string>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }
            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                if (diagnostic.Severity < minimum || diagnostic.Severity == DiagnosticSeverity.Hidden)
                {
                    continue;
                }
                var position = diagnostic.Location.GetLineSpan();
                var path = position.Path is not null
                    ? Path.GetRelativePath(SelfBuild.SelfBuildPaths.WorkspaceRoot, position.Path)
                    : "?";
                results.Add($"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Id} " +
                           $"{path}:{position.StartLinePosition.Line + 1}: {diagnostic.GetMessage()}");
                if (results.Count >= 100)
                {
                    break;
                }
            }
            if (results.Count >= 100)
            {
                break;
            }
        }

        var builder = new StringBuilder(string.Join('\n', results));
        if (results.Count >= 100)
        {
            builder.Append("\n... (truncated at 100 diagnostics)");
        }
        return results.Count > 0 ? builder.ToString() : "no diagnostics";
    }
}
