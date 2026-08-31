using Microsoft.CodeAnalysis;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.SelfBuild;

[Tool("rebuild_self",
    "Rebuild the QwenPlayground application itself from source and restart into the new version. " +
    "Use after modifying the application's own code. Runs Roslyn error check first, then build and tests. " +
    "On failure returns the errors; fix them and call again. " +
    "NOTE: If Roslyn reports errors in WPF XAML-generated fields (e.g. 'Name does not exist in context' for XAML x:Name fields), " +
    "this is a known limitation — Roslyn doesn't run the XAML compiler. Build manually with: dotnet build -c Release -o run/<id>")]
public sealed class RebuildSelfTool : AgentTool
{
    // Общий Roslyn-воркспейс — не создавать свой, см. RoslynService.Shared.
    private static readonly Roslyn.RoslynService Service = Roslyn.RoslynService.Shared;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var roslynErrors = await CollectRoslynErrors(cancellationToken);
        if (roslynErrors.Count > 0)
        {
            return $"Error: Roslyn reports {roslynErrors.Count} compilation errors; fix them before rebuilding:\n" +
                   string.Join('\n', roslynErrors);
        }

        var result = await SelfBuildService.BuildNextAsync(cancellationToken);
        if (result.ExitCode != 0)
        {
            return $"Error: build failed (exit code {result.ExitCode}). Fix the errors and call rebuild_self again.\n{result.OutputTail}";
        }

        // Git status: где мы, что запушено
        var gitInfo = GetGitStatus();

        SelfBuildService.RequestRestart(result.Id);
        return $"Build {result.Id} succeeded. The application will now restart into the new version.\n" +
               $"Git: {gitInfo}";
    }

    /// <summary>Краткий git-статус для логирования в результат rebuild.</summary>
    private static string GetGitStatus()
    {
        try
        {
            var root = SelfBuildPaths.WorkspaceRoot;
            var head = RunGit(root, "log -1 --oneline");
            var remote = RunGit(root, "rev-parse @{u}");
            var local = RunGit(root, "rev-parse HEAD");
            var pushed = string.Equals(remote, local, StringComparison.OrdinalIgnoreCase);
            return string.IsNullOrEmpty(head)
                ? "not a git repo"
                : $"{head} (pushed: {(pushed ? "yes" : "NO — internet may be down")})";
        }
        catch
        {
            return "git unavailable";
        }
    }

    private static string RunGit(string workingDir, string args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null) return "";
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(10000);
        return output;
    }

    private static async Task<List<string>> CollectRoslynErrors(CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        var errors = new List<string>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }
            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error)
                {
                    continue;
                }
                var position = diagnostic.Location.GetLineSpan();
                var path = position.Path is not null
                    ? Path.GetRelativePath(SelfBuildPaths.WorkspaceRoot, position.Path)
                    : "?";
                errors.Add($"{diagnostic.Id} {path}:{position.StartLinePosition.Line + 1}: {diagnostic.GetMessage()}");
                if (errors.Count >= 50)
                {
                    return errors;
                }
            }
        }
        return errors;
    }
}
