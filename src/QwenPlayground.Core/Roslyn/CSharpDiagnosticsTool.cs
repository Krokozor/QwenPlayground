using System.Text;
using Microsoft.CodeAnalysis;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

[Tool("csharp_diagnostics",
    "Get Roslyn compilation diagnostics (errors/warnings) for the tracked C# project without " +
    "building it (the QwenPlayground solution when the working folder is the harness root; " +
    "otherwise the project set via csharp_track). Paths are relative to the working folder. " +
    "Optional 'project' filter: only projects whose name contains the substring (e.g. 'Core').",
    ToolGroup.CSharp)]
public sealed class CSharpDiagnosticsTool : AgentTool
{
    [ToolParameter("Minimum severity: error, warning or info")]
    public string Severity { get; set; } = "warning";

    [ToolParameter("Optional: only projects whose name contains this substring (e.g. 'Core').")]
    public string ProjectFilter { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (CSharpProjects.Resolve(context.ProjectRoot, out var error) is not { } csharp)
        {
            return error!;
        }
        var minimum = Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Error
            : Severity.Equals("info", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Info
            : DiagnosticSeverity.Warning;

        var solution = await csharp.Service.GetSolutionAsync(cancellationToken);
        var root = context.ProjectRoot;
        var results = new List<string>();

        foreach (var project in solution.Projects)
        {
            if (!string.IsNullOrWhiteSpace(ProjectFilter) &&
                !project.Name.Contains(ProjectFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
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
                    ? LocationFormatter.Relative(position.Path, root)
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
