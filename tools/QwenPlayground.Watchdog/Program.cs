using QwenPlayground.Core.Crash;

// QwenPlayground.Watchdog — молчаливый страж процесса приложения.
//
// Запускается самим приложением при старте (WatchdogLauncher.TryStart):
//   QwenPlayground.Watchdog <pid> <processName> <cleanMarker> <logsDir>
//
// Ждёт, пока процесс приложения завершится. Два исхода:
// - чистый маркер (приложение записало его в OnExit) → уходим молча;
// - маркера нет → процесс умер неконтролируемо (нативный краш, kill, OOM —
//   всё, что обходит managed-обработчики CrashLog). Пишем запись в канал
//   приложения: exit code (если удалось прочитать) + выжимку Windows Event Log
//   (единственный след нативной смерти) + указатель на возможную
//   managed-запись выше в файле.
//
// Сам watchdog — тривиальный цикл без исключений: любая ошибка не должна
// помешать ему дождаться смерти процесса.

if (args.Length < 4 || !int.TryParse(args[0], out var pid))
{
    Console.Error.WriteLine("usage: QwenPlayground.Watchdog <pid> <processName> <cleanMarker> <logsDir>");
    return 2;
}

var (processName, cleanMarker, logsDir) = (args[1], args[2], args[3]);

Trace($"started, watching pid {pid} ({processName})");

// Монитор захватывает Process-объект пока процесс жив: в .NET 10 GetProcessById
// мёртвый процесс уже не вернёт, и код выхода читается только с удержанного хэндля.
var monitor = new WatchdogMonitor(pid, processName);

while (true)
{
    WatchdogOutcome outcome;
    try
    {
        outcome = monitor.PollOnce(cleanMarker);
    }
    catch
    {
        // Опрос не должен умирать: повторим через секунду.
        Thread.Sleep(1000);
        continue;
    }

    switch (outcome)
    {
        case WatchdogOutcome.Continue:
            Thread.Sleep(1000);
            break;

        case WatchdogOutcome.CleanExit:
            Trace("process exited cleanly (marker) — exiting");
            return 0;

        case WatchdogOutcome.Died:
            RecordDeath(monitor.ExitCode, processName, logsDir);
            Trace($"process died without clean marker (exit code: {monitor.ExitCode?.ToString() ?? "unknown"}) — exiting");
            return 0;
    }
}

void RecordDeath(int? exitCode, string processName, string logsDir)
{
    var details = new List<string>
    {
        $"exit code: {exitCode?.ToString() ?? "unknown"}; clean marker: absent (unplanned termination)",
        "If a managed crash was logged, the previous entry in this file has the details."
    };
    var excerpt = EventLogExcerpt.ForProcess(processName);
    if (excerpt is not null)
    {
        details.Add("Windows Event Log (recent errors):\n" + excerpt);
    }
    // processName/pid — наблюдаемый процесс: в «Process» записи должен быть он,
    // а не сам watchdog (watchdog — только инструмент записи).
    CrashLogCore.WriteWithContext(logsDir, CrashLogCore.AppChannel,
        "Watchdog: process died unexpectedly", null, string.Join("\n\n", details),
        processName: processName, pid: pid);
}

void Trace(string message)
{
    try
    {
        File.AppendAllText(Path.Combine(logsDir, "watchdog.log"),
            $"[{DateTime.Now:O}] {message}\n");
    }
    catch
    {
        // трейс — не критичен
    }
}
