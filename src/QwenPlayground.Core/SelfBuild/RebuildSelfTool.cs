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

        // Git status: где мы, что запушено. Пуш — только если включён в настройках.
        var gitInfo = GetGitStatus();
        var pushInfo = MaybePush();

        SelfBuildService.RequestRestart(result.Id);
        return $"Build {result.Id} succeeded. The application will now restart into the new version.\n" +
               $"Git: {gitInfo}" + (pushInfo is null ? string.Empty : $"\n{pushInfo}");
    }

    /// <summary>
    /// Краткий git-статус для логирования в результат rebuild: где HEAD и сколько коммитов
    /// не запушено (только КОНСТАТАЦИЯ — пуш не делает; пуш — MaybePush по настройке).
    /// </summary>
    private static string GetGitStatus()
    {
        var root = SelfBuildPaths.WorkspaceRoot;
        var (_, head) = RunGit(root, "log -1 --oneline");
        if (string.IsNullOrEmpty(head))
        {
            return "not a git repo";
        }
        var (_, upstream) = RunGit(root, "rev-parse --verify -q @{u}");
        if (string.IsNullOrEmpty(upstream))
        {
            return $"{head} (no upstream)";
        }
        var (_, unpushed) = RunGit(root, "rev-list --count @{u}..HEAD");
        return unpushed == "0"
            ? $"{head} (up-to-date with origin)"
            : $"{head} ({unpushed} unpushed commit(s))";
    }

    /// <summary>
    /// Пуш после успешного билда, только если включён в настройках (PushOnRebuild, по умолчанию
    /// выкл): git push уже закоммиченных коммитов. Инструмент НЕ коммитит — коммиты делает
    /// владелец/агент явно. null — пуш выключен.
    /// Направление: PushRepo (URL/remote, например форк) — если задан; иначе текущий upstream.
    /// </summary>
    private static string? MaybePush()
    {
        var settings = Settings.AppSettings.Get();
        if (!settings.PushOnRebuild)
        {
            return null;
        }
        var root = SelfBuildPaths.WorkspaceRoot;

        string pushArgs;
        if (!string.IsNullOrWhiteSpace(settings.PushRepo))
        {
            // Явное направление (форк): HEAD текущей ветки → main цели.
            pushArgs = $"push {settings.PushRepo.Trim()} HEAD:main";
        }
        else
        {
            var (_, upstream) = RunGit(root, "rev-parse --verify -q @{u}");
            if (string.IsNullOrEmpty(upstream))
            {
                return "push: no upstream — skipped (укажите «куда пушить» в настройках или установите upstream)";
            }
            pushArgs = "push";
        }

        var (code, output) = RunGit(root, pushArgs, timeoutMs: 60000);
        return code == 0
            ? $"push: ok ({(output.Length > 0 ? output : "nothing to push")})"
            : $"push: FAILED (exit {code}) — {output}";
    }

    private static (int ExitCode, string Output) RunGit(string workingDir, string args, int timeoutMs = 10000)
    {
        try
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
            if (process is null) return (-1, "");
            // git пишет результат в stderr (push: "To https://…") — читаем оба потока.
            var stdout = process.StandardOutput.ReadToEnd().Trim();
            var stderr = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit(timeoutMs);
            var output = (stdout + (stderr.Length > 0 ? (stdout.Length > 0 ? " " : "") + stderr : "")).Trim();
            return (process.ExitCode, output);
        }
        catch
        {
            return (-1, "");
        }
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
