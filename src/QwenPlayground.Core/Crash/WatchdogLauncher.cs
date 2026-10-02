using System.Diagnostics;
using System.IO;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Crash;

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

    /// <summary>
    /// Rebuild-окно (BeginRebuild/EndRebuild): пока true, EnsureAlive не
    /// воскрешает watchdog'а — сборка должна спокойно обновить бинари launcher/.
    /// </summary>
    private static volatile bool _rebuildInProgress;

    public static void TryStart()
    {
        try
        {
            var exe = Path.Combine(SelfBuildPaths.LauncherDir, WatchdogExeName);
            if (!File.Exists(exe))
            {
                SelfBuild.RebuildEventLog.App("watchdog: not started (exe missing — dev build)");
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
            SelfBuild.RebuildEventLog.App($"watchdog: started (pid {_watchdog?.Id}, watching app pid {Environment.ProcessId})");
        }
        catch
        {
            // Watchdog — вспомогательная страховка: его отсутствие не должно ломать приложение.
            SelfBuild.RebuildEventLog.App("watchdog: start FAILED (exception) — app runs without guardian");
        }
    }

    /// <summary>
    /// Открыть rebuild-окно: запретить EnsureAlive воскрешать watchdog'а и
    /// остановить всех watchdog'ов. Закрывается <see cref="EndRebuild"/> — ОБЯЗАТЕЛЬНО,
    /// в т.ч. в finally: иначе страж не восстановится (тихая деградация).
    /// </summary>
    public static void BeginRebuild()
    {
        _rebuildInProgress = true;
        SelfBuild.RebuildEventLog.App("rebuild window: OPENED (EnsureAlive suspended, all watchdogs stopped)");
        StopAllByName();
    }

    /// <summary>
    /// Закрыть rebuild-окно: разрешить EnsureAlive снова и, если watchdog не
    /// работает, — запустить (старым бинарем, если сборка упала, новым — если
    /// прошла). Страж не должен молча теряться ни в одном исходе.
    /// </summary>
    public static void EndRebuild()
    {
        _rebuildInProgress = false;
        if (!IsAnyWatchdogRunning())
        {
            TryStart();
            SelfBuild.RebuildEventLog.App("rebuild window: CLOSED (watchdog was absent — started)");
        }
        else
        {
            SelfBuild.RebuildEventLog.App("rebuild window: CLOSED (watchdog already running)");
        }
    }

    private static bool IsAnyWatchdogRunning()
    {
        foreach (var process in Process.GetProcessesByName("QwenPlayground.Watchdog"))
        {
            process.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>Публичный доступ для pre-rebuild проверки страховки (RebuildSelfTool).</summary>
    public static bool IsWatchdogRunning() => IsAnyWatchdogRunning();

    /// <summary>
    /// Остановить ВСЕ watchdog'и по имени процесса (без хэндлов). Для внешних
    /// вызывателей (лаунчер: watchdog — не его ребёнок, хэндла нет). Остановленный
    /// watchdog не «потеряет» ничего: он либо уже записал смерть наблюдаемого процесса,
    /// либо тот ещё жив и его стражем станет новый watchdog после деплоя.
    /// </summary>
    public static void StopAllByName()
    {
        foreach (var process in Process.GetProcessesByName("QwenPlayground.Watchdog"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
                // не остановился — деплой сам упадёт с записью в launcher.log
            }
            finally
            {
                process.Dispose();
            }
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
            // Rebuild-окно: watchdog убит намеренно (деплой инструментов),
            // воскрешать его сейчас = закрыть лок на бинари, которые собираются.
            if (_rebuildInProgress)
            {
                return;
            }
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
            CrashLogCore.WriteWithContext(CrashLogCore.DefaultLogsDir, CrashLogCore.AppChannel,
                "Watchdog: guardian died", null,
                $"watchdog (PID {watchdog.Id}) завершился, пока приложение живо (exit code: {exitCode?.ToString() ?? "unknown"}). " +
                "Пока страж был мёртв, неконтролируемые смерти не фиксировались. Страж перезапущен.");
            watchdog.Dispose();
            _watchdog = null;
            TryStart();
            SelfBuild.RebuildEventLog.App($"watchdog: EnsureAlive — guardian died (exit {exitCode?.ToString() ?? "unknown"}), restarted (pid {_watchdog?.Id})");
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
