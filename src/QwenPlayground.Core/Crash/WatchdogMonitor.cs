using System.Diagnostics;

namespace QwenPlayground.Core.Crash;

public enum WatchdogOutcome
{
    /// <summary>Процесс жив — продолжаем ждать.</summary>
    Continue,
    /// <summary>Процесс завершился чисто (маркер есть) — watchdog уходит молча.</summary>
    CleanExit,
    /// <summary>Процесс умер без чистого маркера — watchdog фиксирует смерть.</summary>
    Died,
}

/// <summary>
/// Наблюдатель процесса watchdog'а — отдельного процесса, который дожидается
/// смерти приложения. Почему нужен: managed-краши ловит CrashLog (Dispatcher/AppDomain),
/// но нативные смерти (access violation, OOM-kill, kill извне) обходят все обработчики —
/// процесс просто исчезает, и «почему» остаётся только в Windows Event Log.
/// Watchdog видит и то, и другое: для него разница лишь в наличии чистого маркера.
///
/// ВАЖНО (.NET 10): Process.GetProcessById для УЖЕ завершённого процесса бросает
/// ArgumentException (зомби не возвращается). Поэтому монитор захватывает Process-объект
/// (и хэндль) пока процесс жив, и дальше наблюдает именно его: HasExited/ExitCode
/// читаются с удержанного объекта. Если процесс умер до захвата — код выхода
/// неизвестен, но сама смерть фиксируется.
///
/// Логика отделена от цикла (Program watchdog'а) ради тестов.
/// </summary>
public sealed class WatchdogMonitor
{
    /// <summary>Маркер старше этого срока не считается «чистым» (остаток от другого запуска).</summary>
    public static readonly TimeSpan MarkerMaxAge = TimeSpan.FromMinutes(30);

    private readonly int _pid;
    private readonly string _processName;
    private Process? _tracked;

    /// <summary>Код выхода, если удалось прочитать (с удержанного Process-объекта); null — неизвестен.</summary>
    public int? ExitCode { get; private set; }

    public WatchdogMonitor(int pid, string processName)
    {
        _pid = pid;
        _processName = processName;
        try
        {
            var process = Process.GetProcessById(pid);
            string? name;
            try
            {
                name = process.ProcessName;
            }
            catch
            {
                process.Dispose();
                return; // нет прав на опрос — путь смерти разберётся без захвата
            }
            if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
            {
                _tracked = process; // хэндль держим: он и есть наш «канал» до кода выхода
            }
            else
            {
                process.Dispose(); // pid уже занят чужим процессом
            }
        }
        catch
        {
            // Процесс не найден (умер до захвата) — PollOnce сразу пойдёт в смерть.
        }
    }

    /// <summary>
    /// Один опрос:
    /// - отслеживаемый процесс жив → <see cref="WatchdogOutcome.Continue"/>;
    /// - умер (или никогда не был захвачен) → смотрим маркер:
    ///   свежий → <see cref="WatchdogOutcome.CleanExit"/> (маркер удаляется),
    ///   нет/протух → <see cref="WatchdogOutcome.Died"/>.
    /// </summary>
    public WatchdogOutcome PollOnce(string cleanMarkerPath)
    {
        if (_tracked is not null)
        {
            bool exited;
            try
            {
                exited = _tracked.HasExited;
            }
            catch
            {
                exited = true;
            }
            if (!exited)
            {
                return WatchdogOutcome.Continue;
            }
            // Код выхода читаем ДО Dispose: пока наш хэндль открыт, зомби процесса
            // жив и OpenProcess его найдёт. Process.ExitCode здесь не работает
            // (.NET 10: «Process was not started by this object») — см. NativeExitCode.
            ExitCode = OperatingSystem.IsWindows() ? NativeExitCode.TryGet(_pid) : null;
            _tracked.Dispose();
            _tracked = null;
        }
        return ConcludeDeath(cleanMarkerPath);
    }

    private WatchdogOutcome ConcludeDeath(string cleanMarkerPath)
    {
        try
        {
            if (File.Exists(cleanMarkerPath))
            {
                var age = DateTime.Now - File.GetLastWriteTime(cleanMarkerPath);
                File.Delete(cleanMarkerPath);
                if (age < MarkerMaxAge)
                {
                    return WatchdogOutcome.CleanExit;
                }
                // Протухший маркер от другого запуска — не повод пропустить смерть.
            }
        }
        catch
        {
            // Ошибка с маркером не повод пропустить фиксацию смерти.
        }
        return WatchdogOutcome.Died;
    }
}
