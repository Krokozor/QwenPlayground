using System.Diagnostics;
using System.Text;

namespace QwenPlayground.Core.SelfBuild;

public sealed record BuildResult(string Id, int ExitCode, string OutputTail);

public static class SelfBuildService
{
    public static async Task<BuildResult> BuildNextAsync(CancellationToken cancellationToken)
    {
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        // Pointer-layout: версия собирается в свой неизменяемый каталог run/<id>.
        // Активацией занимается Launcher (current.txt) — мы каталоги не трогаем.
        var versionDir = SelfBuildPaths.VersionDir(id);
        if (Directory.Exists(versionDir))
        {
            Directory.Delete(versionDir, recursive: true);
        }
        Directory.CreateDirectory(versionDir);

        var buildLogPath = Path.Combine(versionDir, "build.log");
        var build = await RunProcessAsync("dotnet",
            $"build \"{SelfBuildPaths.AppProject}\" -c Release -o \"{versionDir}\"",
            cancellationToken);
        File.WriteAllText(buildLogPath, build.Output);

        if (build.ExitCode != 0)
        {
            var tail = Tail(build.Output);
            BuildJournal.Append(SelfBuildPaths.RunRoot, new BuildJournalEntry
            {
                Id = id,
                Timestamp = DateTime.Now,
                BuildExitCode = build.ExitCode,
                BuildOutputTail = tail,
                Status = "failed",
                FailureReason = $"dotnet build exit code {build.ExitCode}",
                Announced = true,
                BuildLogPath = buildLogPath
            });
            return new BuildResult(id, build.ExitCode, tail);
        }

        // verbose-логгер: при падении в журнал попадает текст ошибки (с -v q он обрезался).
        var gateArgs = $"test \"{SelfBuildPaths.TestProject}\" --nologo -v q --logger \"console;verbosity=normal\"";

        var gateLogPath = Path.Combine(versionDir, "gate.log");
        var gate = await RunProcessAsync("dotnet", gateArgs, cancellationToken);
        var gateAttempt = 1;
        if (gate.ExitCode != 0)
        {
            // Flaky-гейт: сразу после build RoslynServiceTests иногда падает на первом прогоне
            // (конкуренция MSBuild). Настоящий сбой упадёт и на повторе.
            File.WriteAllText(gateLogPath, gate.Output);
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            gate = await RunProcessAsync("dotnet", gateArgs, cancellationToken);
            gateAttempt = 2;
        }
        File.WriteAllText(gateLogPath, gate.Output);

        if (gate.ExitCode != 0)
        {
            var diag = CaptureDiagnostics();
            var gateTail = $"build OK, gate FAILED (tests, attempt {gateAttempt}):\n" + Tail(gate.Output, 6000);
            BuildJournal.Append(SelfBuildPaths.RunRoot, new BuildJournalEntry
            {
                Id = id,
                Timestamp = DateTime.Now,
                BuildExitCode = 0,
                BuildOutputTail = gateTail,
                Status = "failed",
                FailureReason = $"gate: tests failed (exit code {gate.ExitCode}, attempt {gateAttempt})",
                Announced = true,
                BuildLogPath = buildLogPath,
                GateLogPath = gateLogPath,
                GateExitCode = gate.ExitCode
            });
            File.WriteAllText(Path.Combine(versionDir, "gate-diagnostics.txt"), diag);
            return new BuildResult(id, gate.ExitCode, gateTail);
        }

        // Лаунчер в отдельной папке (launcher/), вне run/. Если бы он деплоился в run/,
        // повторная сборка Core (общий obj\Release + -o override на весь граф) вычищала бы
        // Core.dll из только что собранной папки версии.
        await DeployLauncherAsync(cancellationToken);

        BuildJournal.Append(SelfBuildPaths.RunRoot, new BuildJournalEntry
        {
            Id = id,
            Timestamp = DateTime.Now,
            BuildExitCode = 0,
            BuildOutputTail = Tail(build.Output),
            Status = "pending",
            BuildLogPath = buildLogPath,
            GateLogPath = gateLogPath,
            GateExitCode = 0
        });
        return new BuildResult(id, 0, Tail(build.Output));
    }

    /// <summary>Сборка лаунчера в его собственный каталог launcher/ (вне run/).</summary>
    private static async Task DeployLauncherAsync(CancellationToken cancellationToken)
    {
        try
        {
            var launcherProject = Path.Combine(
                SelfBuildPaths.WorkspaceRoot, @"tools\QwenPlayground.Launcher\QwenPlayground.Launcher.csproj");
            var build = await RunProcessAsync(
                "dotnet", $"build \"{launcherProject}\" -c Release -o \"{SelfBuildPaths.LauncherDir}\"", cancellationToken);
            if (build.ExitCode != 0)
            {
                File.AppendAllText(Path.Combine(SelfBuildPaths.RunRoot, "launcher.log"),
                    $"[{DateTime.Now:O}] launcher deploy failed (exit {build.ExitCode}):\n{Tail(build.Output)}\n");
            }
        }
        catch (Exception exception)
        {
            File.AppendAllText(Path.Combine(SelfBuildPaths.RunRoot, "launcher.log"),
                $"[{DateTime.Now:O}] launcher deploy skipped: {exception.Message}\n");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = SelfBuildPaths.WorkspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            // Без Kill осиротевший dotnet build/test продолжал бы держать obj/ и ломать
            // следующие сборки (см. ShellTool — тот же паттерн).
            TryKillProcessTree(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Ход отменён пользователем: процесс-сирота недопустим тем более.
            TryKillProcessTree(process);
            throw;
        }
        return (process.ExitCode, output.ToString());
    }

    /// <summary>Процесс мог выйти между исключением и Kill — не мешаем пробросу уборкой.</summary>
    private static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string Tail(string text, int cap = 3000) =>
        text.Length <= cap ? text : "...\n" + text[^cap..];

    /// <summary>
    /// Снимок окружения при падении гейта: dotnet/MSBuild-процессы, память.
    /// Для диагностики конкуренции (flaky RoslynServiceTests).
    /// </summary>
    private static string CaptureDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Diagnostics captured at {DateTime.Now:O}");
        sb.AppendLine();

        // dotnet и MSBuild процессы
        sb.AppendLine("=== dotnet / MSBuild processes ===");
        try
        {
            var processes = Process.GetProcessesByName("dotnet")
                .Concat(Process.GetProcessesByName("MSBuild"))
                .ToList();
            if (processes.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            foreach (var p in processes)
            {
                using (p)
                {
                    sb.AppendLine($"PID={p.Id} Name={p.ProcessName} WorkingSet={p.WorkingSet64 / 1024 / 1024}MB StartTime={p.StartTime:O}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(error: {ex.Message})");
        }
        sb.AppendLine();

        // Свободная память
        sb.AppendLine("=== Memory ===");
        try
        {
            var gc = GC.GetGCMemoryInfo();
            sb.AppendLine($"TotalAvailableMemoryBytes={gc.TotalAvailableMemoryBytes / 1024 / 1024}MB");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(error: {ex.Message})");
        }

        return sb.ToString();
    }

    public static void RequestRestart(string buildId, string? file = null) =>
        File.WriteAllText(file ?? SelfBuildPaths.RestartRequestFile, buildId);

    public static string? ConsumeRestartRequest(string? file = null)
    {
        var path = file ?? SelfBuildPaths.RestartRequestFile;
        if (!File.Exists(path))
        {
            return null;
        }
        var buildId = File.ReadAllText(path).Trim();
        File.Delete(path);
        return buildId.Length > 0 ? buildId : null;
    }

    public static void WriteHandshake()
    {
        if (SelfBuildPaths.TryGetDeployedRunRoot(out _))
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ok.marker"), DateTime.Now.ToString("O"));
        }
    }
}
