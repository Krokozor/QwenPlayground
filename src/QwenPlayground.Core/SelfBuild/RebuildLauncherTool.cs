using System.Diagnostics;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.SelfBuild;

[Tool("rebuild_launcher",
    "Rebuild the QwenPlayground launcher and watchdog from source into launcher/. " +
    "Use after modifying the launcher's code (e.g. adding a new external tool to the config, " +
    "or changing the launcher's logic). The GUI launcher must be CLOSED (it locks the binary in " +
    "launcher/) — if it is running, close it first and call again. No restart: the launcher is just " +
    "rebuilt; reopen the GUI to use the new version.")]
public sealed class RebuildLauncherTool : AgentTool
{
    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        // GUI-лаунчер держит лок на бинарник launcher/ — если запущен, билд упадёт (MSB3026/3027).
        var launcherProcesses = Process.GetProcessesByName("QwenPlayground.Launcher");
        if (launcherProcesses.Length > 0)
        {
            foreach (var p in launcherProcesses)
            {
                p.Dispose();
            }
            return "Error: the GUI launcher (QwenPlayground.Launcher.exe) is running — it locks the " +
                   "binary in launcher/. Close it and call rebuild_launcher again.";
        }

        // Watchdog держит лок на QwenPlayground.Core.dll в launcher/ — останавливаем перед
        // билдом (PreDeployTools), перезапускаем после (PostDeployTools).
        try
        {
            SelfBuildService.PreDeployTools?.Invoke();
        }
        catch
        {
            // watchdog не остановился — билд сам упадёт с записью лока.
        }

        // Билд лаунчера + watchdog в launcher/ (та же команда, что в bootstrap.bat).
        var launcherProject = Path.Combine(
            SelfBuildPaths.WorkspaceRoot, @"tools\QwenPlayground.Launcher\QwenPlayground.Launcher.csproj");
        var watchdogProject = Path.Combine(
            SelfBuildPaths.WorkspaceRoot, @"tools\QwenPlayground.Watchdog\QwenPlayground.Watchdog.csproj");
        var outputDir = SelfBuildPaths.LauncherDir;

        var (launcherCode, launcherOutput) = await RunDotnetBuild(launcherProject, outputDir, cancellationToken);
        if (launcherCode != 0)
        {
            // Билд упал — watchdog всё равно перезапускаем (не бросаем приложение без стража).
            try { SelfBuildService.PostDeployTools?.Invoke(); } catch { }
            return $"Error: launcher build failed (exit {launcherCode}).\n{launcherOutput}";
        }

        var (watchdogCode, watchdogOutput) = await RunDotnetBuild(watchdogProject, outputDir, cancellationToken);
        if (watchdogCode != 0)
        {
            try { SelfBuildService.PostDeployTools?.Invoke(); } catch { }
            return $"Error: watchdog build failed (exit {watchdogCode}).\n{watchdogOutput}";
        }

        // Билд успешен — перезапускаем watchdog (уже с новым бинаром).
        try
        {
            SelfBuildService.PostDeployTools?.Invoke();
        }
        catch
        {
            // watchdog не перезапущен — EnsureAlive (heartbeat) подхватит в течение 20 с.
        }

        return "Launcher and watchdog rebuilt successfully into launcher/. " +
               "Reopen the GUI launcher to use the new version.";
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetBuild(
        string project, string outputDir, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo("dotnet", $"build \"{project}\" -c Release -o \"{outputDir}\"")
            {
                WorkingDirectory = SelfBuildPaths.WorkspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, "failed to start dotnet");
            }
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (stdout + (stderr.Length > 0 ? (stdout.Length > 0 ? "\n" : "") + stderr : "")).Trim();
            // dotnet build пишет много шума — оставляем хвост (там ошибки, если они есть).
            if (output.Length > 4096)
            {
                output = "..." + output[^4096..];
            }
            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
