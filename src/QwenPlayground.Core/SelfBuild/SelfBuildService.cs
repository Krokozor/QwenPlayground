using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace QwenPlayground.Core.SelfBuild;

public sealed record BuildResult(string Id, int ExitCode, string OutputTail, string? Warnings = null);

/// <summary>Упавший тест (из TRX): имя и первое сообщение ошибки.</summary>
public sealed record FailedTest(string Name, string Error);

public static class SelfBuildService
{
    /// <summary>
    /// Хук перед деплоем инструментов (лаунчер/watchdog в launcher/): приложение
    /// подставляет остановку своего watchdog'а — тот держит бинари launcher/
    /// (Windows-лок), и деплой не смог бы обновить их под живым стражем.
    /// </summary>
    public static Action? PreDeployTools;

    /// <summary>
    /// Хук после деплоя инструментов: приложение подставляет перезапуск watchdog'а
    /// (он был остановлен PreDeployTools). Вызывается rebuild_launcher после сборки.
    /// </summary>
    public static Action? PostDeployTools;

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
        // TRX-логгер: структурированный (локаль-независимый) список упавших тестов — для
        // отчёта «какой тест не прошёл» в начале. Консольный — для читаемого tail.
        var gateTrxPath = Path.Combine(versionDir, "gate-results.trx");
        var gateArgs = $"test \"{SelfBuildPaths.TestProject}\" --nologo -v q --logger \"console;verbosity=normal\" --logger \"trx;LogFileName={gateTrxPath}\"";

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
            var failedTests = ParseFailedTests(gateTrxPath);
            var gateTail = BuildTestFailureReport(gateAttempt, failedTests, gate.Output);
            BuildJournal.Append(SelfBuildPaths.RunRoot, new BuildJournalEntry
            {
                Id = id,
                Timestamp = DateTime.Now,
                BuildExitCode = 0,
                BuildOutputTail = gateTail,
                Status = "failed",
                FailureReason = $"gate: tests failed (exit code {gate.ExitCode}, attempt {gateAttempt}, {failedTests.Count} test(s))",
                Announced = true,
                BuildLogPath = buildLogPath,
                GateLogPath = gateLogPath,
                GateExitCode = gate.ExitCode
            });
            File.WriteAllText(Path.Combine(versionDir, "gate-diagnostics.txt"), diag);
            return new BuildResult(id, gate.ExitCode, gateTail);
        }

        // Лаунчер и watchdog в отдельной папке (launcher/), вне run/: если бы они
        // деплоились в run/, повторная сборка Core (общий obj\Release + -o override
        // на весь граф) вычищала бы Core.dll из только что собранной папки версии.
        // Перед деплоем — хук PreDeployTools: приложение останавливает своего
        // watchdog'а, освобождающего бинари launcher/.
        try
        {
            PreDeployTools?.Invoke();
        }
        catch
        {
            // Хук не должен ломать сборку: если watchdog не остановился, деплой
            // сам упадёт с записью в launcher.log.
        }
        await DeployLauncherAsync(cancellationToken);
        await DeployWatchdogAsync(cancellationToken);

        // Ворнинги сборки и гейта — в отчёт (НЕ блокируют: pass остаётся pass для пуша,
        // но предупреждения видны сразу, а не «где-то в build.log»).
        var warnings = ExtractWarnings(build.Output + "\n" + gate.Output);
        var finalTail = Tail(build.Output) + (warnings ?? string.Empty);
        BuildJournal.Append(SelfBuildPaths.RunRoot, new BuildJournalEntry
        {
            Id = id,
            Timestamp = DateTime.Now,
            BuildExitCode = 0,
            BuildOutputTail = finalTail,
            Status = "pending",
            BuildLogPath = buildLogPath,
            GateLogPath = gateLogPath,
            GateExitCode = 0
        });
        return new BuildResult(id, 0, finalTail, warnings);
    }

    /// <summary>
    /// Строки «: warning CODE:» из вывода dotnet, дедуплицированные (одно предупреждение
    /// печатается по разу на каждый проект графа). null — предупреждений нет.
    /// </summary>
    private static string? ExtractWarnings(string dotnetOutput)
    {
        var lines = dotnetOutput.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(": warning ", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        if (lines.Count == 0)
        {
            return null;
        }
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"Build warnings: {lines.Count} (non-blocking — pass stays pass, but see them):");
        foreach (var line in lines.Take(20))
        {
            sb.AppendLine("  " + line);
        }
        if (lines.Count > 20)
        {
            sb.AppendLine($"  ... ({lines.Count - 20} more in build.log)");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Сборка watchdog'а в launcher/ (сиблинг run/). Watchdog — страж процесса:
    /// фиксирует смерти, которые обходят managed-обработчики (нативные краши).
    /// Перед деплоем вызывается <see cref="PreDeployTools"/>: приложение останавливает
    /// своего watchdog'а, иначе тот держит бинари launcher/ (Windows-лок) и деплой
    /// не смог бы их обновить.
    /// </summary>
    private static async Task DeployWatchdogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var project = Path.Combine(
                SelfBuildPaths.WorkspaceRoot, @"tools\QwenPlayground.Watchdog\QwenPlayground.Watchdog.csproj");
            var build = await RunProcessAsync(
                "dotnet", $"build \"{project}\" -c Release -o \"{SelfBuildPaths.LauncherDir}\"", cancellationToken);
            if (build.ExitCode != 0)
            {
                File.AppendAllText(Path.Combine(SelfBuildPaths.RunRoot, "launcher.log"),
                    $"[{DateTime.Now:O}] watchdog deploy failed (exit {build.ExitCode}):\n{Tail(build.Output)}\n");
            }
        }
        catch (Exception exception)
        {
            File.AppendAllText(Path.Combine(SelfBuildPaths.RunRoot, "launcher.log"),
                $"[{DateTime.Now:O}] watchdog deploy skipped: {exception.Message}\n");
        }
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
    /// Отчёт о падении тест-гейта: НАЧАЛО — список упавших тестов (из TRX, локаль-независимо),
    /// потом tail консоли. Чтобы «что не прошло» было видно сразу, без копания в хвосте.
    /// </summary>
    private static string BuildTestFailureReport(int attempt, List<FailedTest> failedTests, string consoleOutput)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"build OK, gate FAILED (tests, attempt {attempt}): {failedTests.Count} test(s) failed");
        if (failedTests.Count > 0)
        {
            sb.AppendLine("FAILED TESTS:");
            foreach (var (name, error) in failedTests)
            {
                sb.AppendLine($"  - {name}");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    foreach (var line in error.Replace("\r", string.Empty).Split('\n')
                                 .Where(l => l.Trim().Length > 0).Take(3))
                    {
                        sb.AppendLine($"      {line.Trim()}");
                    }
                }
            }
        }
        else
        {
            sb.AppendLine("(TRX не распарсен — детали ниже, в console tail)");
        }
        sb.AppendLine();
        sb.AppendLine("--- console tail ---");
        sb.AppendLine(Tail(consoleOutput, 4000));
        return sb.ToString();
    }

    /// <summary>
    /// Извлечь упавшие тесты из TRX (локаль-независимо: по атрибуту outcome="Failed").
    /// Возвращает (testName, сообщение ошибки). Пустой список — TRX нет/не распарсился.
    /// </summary>
    private static List<FailedTest> ParseFailedTests(string trxPath)
    {
        var failed = new List<FailedTest>();
        try
        {
            if (!File.Exists(trxPath))
            {
                return failed;
            }
            var doc = XDocument.Load(trxPath);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            foreach (var result in doc.Descendants(ns + "UnitTestResult"))
            {
                var outcome = result.Attribute("outcome")?.Value;
                if (!string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var name = result.Attribute("testName")?.Value ?? "?";
                var message = result.Descendants(ns + "Message").FirstOrDefault()?.Value ?? string.Empty;
                failed.Add(new FailedTest(name, message));
            }
        }
        catch
        {
            // TRX бит/отсутствует — отчёт упадёт на console tail
        }
        return failed;
    }

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
