using System.Diagnostics;
using System.IO;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.App;

/// <summary>
/// Запуск watchdog'а — отдельного процесса, который фиксирует смерть приложения,
/// если оно умрёт без чистого закрытия (нативный краш, kill, OOM — всё, что
/// обходит managed-обработчики CrashLog). Watchdog — deploy-инструмент в launcher/;
/// в dev-сборке его может не быть — тогда приложение работает без стража.
///
/// Протокол: приложение стартует watchdog'а со своим pid и путём к «чистому
/// маркеру»; при чистом выходе (App.OnExit) маркер записывается, watchdog уходит
/// молча. Без маркера watchdog пишет «PROCESS DIED» в общий crash-лог приложения.
///
/// Перед деплоем инструментов (rebuild) приложение останавливает своего watchdog'а
/// (SelfBuildService.PreDeployTools): тот держит бинари launcher/ (Windows-лок),
/// и сборка не смогла бы их обновить.
/// </summary>
public static class WatchdogLauncher
{
    public const string WatchdogExeName = "QwenPlayground.Watchdog.exe";

    private static string? _cleanMarker;
    private static Process? _watchdog;

    public static void TryStart()
    {
        try
        {
            var exe = Path.Combine(SelfBuildPaths.LauncherDir, WatchdogExeName);
            if (!File.Exists(exe))
            {
                return; // dev-сборка без watchdog'а — не ошибка
            }
            var logsDir = Path.Combine(SelfBuildPaths.WorkspaceRoot, "logs");
            _cleanMarker = Path.Combine(SelfBuildPaths.RunRoot, $"clean-{Environment.ProcessId}.txt");
            Directory.CreateDirectory(SelfBuildPaths.RunRoot);
            _watchdog = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{Environment.ProcessId} {Process.GetCurrentProcess().ProcessName} \"{_cleanMarker}\" \"{logsDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            // Watchdog — вспомогательная страховка: его отсутствие не должно ломать приложение.
        }
    }

    /// <summary>
    /// Остановить watchdog'а перед деплоем инструментов (вызывается из
    /// SelfBuildService.PreDeployTools). После перезапуска приложение стартует
    /// watchdog'а заново — уже с новым бинарем.
    /// </summary>
    public static void StopWatchdog()
    {
        try
        {
            var watchdog = _watchdog;
            if (watchdog is null || watchdog.HasExited)
            {
                return;
            }
            watchdog.Kill(entireProcessTree: true);
            watchdog.WaitForExit(5000);
        }
        catch
        {
            // watchdog не остановился — деплой сам упадёт с записью в launcher.log
        }
    }

    /// <summary>
    /// «Watchdog на watchdog'а»: приложение периодически проверяет, жив ли его страж
    /// (раз в 20 с из heartbeat-тика). Страж умер, а приложение живо → это сама по себе
    /// тихая смерть (страж мог умереть, не успев записать чужой краш): фиксируем в
    /// crash-лог и перезапускаем.
    /// </summary>
    public static void EnsureAlive()
    {
        try
        {
            var watchdog = _watchdog;
            if (watchdog is null || !watchdog.HasExited)
            {
                return;
            }
            int? exitCode = null;
            try
            {
                exitCode = watchdog.ExitCode;
            }
            catch
            {
                // exit code не критичен
            }
            CrashLog.LogCrash("Watchdog: guardian died",
                $"watchdog (PID {watchdog.Id}) завершился, пока приложение живо (exit code: {exitCode?.ToString() ?? "unknown"}). " +
                "Пока страж был мёртв, неконтролируемые смерти не фиксировались. Страж перезапущен.");
            watchdog.Dispose();
            _watchdog = null;
            TryStart();
        }
        catch
        {
            // проверка не должна ломать heartbeat-тик
        }
    }

    /// <summary>
    /// Записать маркер чистого завершения (вызывается в App.OnExit, до выхода процесса).
    /// Заодно чистит маркеры старых запусков (старше суток).
    /// </summary>
    public static void MarkClean()
    {
        try
        {
            if (_cleanMarker is not null)
            {
                File.WriteAllText(_cleanMarker, DateTime.Now.ToString("O"));
            }
            var runRoot = SelfBuildPaths.RunRoot;
            if (Directory.Exists(runRoot))
            {
                foreach (var file in Directory.EnumerateFiles(runRoot, "clean-*.txt"))
                {
                    if ((DateTime.Now - File.GetLastWriteTime(file)).TotalHours > 24)
                    {
                        File.Delete(file);
                    }
                }
            }
        }
        catch
        {
            // маркер не записан — watchdog примет смерть за неконтролируемую;
            // лучше перестраховка в лог, чем падение OnExit
        }
    }
}
