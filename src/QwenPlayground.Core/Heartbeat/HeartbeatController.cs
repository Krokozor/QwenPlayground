using QwenPlayground.Core.Runtime;

namespace QwenPlayground.Core.Heartbeat;

/// <summary>
/// Сердцебиение ЧАТА (решение владельца, 2026-10-16: разделение настраиваемого и
/// внешнего): ДВА независимых канала —
/// (1) НАСТРАИВАЕМОЕ расписание (HeartbeatEnabled + интервал) — плановые автономные ходы;
/// (2) ВНЕШНИЕ будильщики — wake/ файлы (только main), pigeon-mention (Wake), кнопка
/// (WakeNow): работают ВСЕГДА, независимо от HeartbeatEnabled, без ожидания тика.
/// Исполнение хода и flush памяти остаются снаружи (нужны FSM, endpoint, настройки) —
/// контроллер владеет только решением «когда и чем разбудить» и статусами. Без таймера:
/// качает UI (DispatcherTimer) или тест (Tick() напрямую); часы — через <paramref name="clock"/>.
/// </summary>
public sealed class HeartbeatController
{
    public const string DefaultPrompt =
        "[heartbeat] Periodic autonomous wake-up. Check trajectory.md (current goal) and refactoring.md (backlog) " +
        "for pending work and decide if anything small and safe is worth doing now. If you find something concrete " +
        "(bug, unfinished item, cleanup), do it with your tools and keep the change focused; if you modified " +
        "application code, finish with rebuild_self. If nothing needs attention, reply with a one-line status and " +
        "stop. Do not start large refactors or risky changes unprompted.";

    private readonly WakeSignalStore? _wakeSignals;
    private readonly Func<bool> _isBusy;
    private readonly Func<bool> _heartbeatEnabled;
    private readonly Func<double> _intervalMinutes;
    private readonly Action<string> _setStatus;
    private readonly Func<string, Task> _startTurn;
    private readonly Func<Task> _flushMemory;
    private readonly Func<DateTime> _utcNow;
    private readonly BackgroundWork _background;
    private readonly Action? _watchdogGuard;

    /// <summary>MinValue нельзя: первый же тик считался бы «просроченным». Стартуем отсчёт от создания.</summary>
    private DateTime _lastTurnAt;

    public HeartbeatController(
        WakeSignalStore? wakeSignals,
        Func<bool> isBusy,
        Func<bool> heartbeatEnabled,
        Func<double> heartbeatIntervalMinutes,
        Action<string> setStatus,
        Func<string, Task> startTurn,
        Func<Task> flushMemory,
        Func<DateTime>? clock = null,
        BackgroundWork? background = null,
        Action? watchdogGuard = null)
    {
        _wakeSignals = wakeSignals;
        _isBusy = isBusy;
        _heartbeatEnabled = heartbeatEnabled;
        _intervalMinutes = heartbeatIntervalMinutes;
        _setStatus = setStatus;
        _startTurn = startTurn;
        _flushMemory = flushMemory;
        _utcNow = clock ?? new Func<DateTime>(() => DateTime.UtcNow);
        _background = background ?? new BackgroundWork(_ => { });
        _watchdogGuard = watchdogGuard;
        _lastTurnAt = _utcNow();
    }

    private int _tickCount;

    /// <summary>
    /// Один тик опроса (качает UI-таймер, 20 с): busy гасит всё; flush памяти — всегда при
    /// свободном чате; внешние wake-сигналы (wake/ файлы) — не ждут HeartbeatEnabled;
    /// плановое расписание — только при включённом heartbeat.
    /// </summary>
    public void Tick()
    {
        if (++_tickCount <= 3)
        {
            QwenPlayground.Core.Crash.StartupTrace.Log($"heartbeat tick #{_tickCount} (busy={_isBusy()})");
        }
        // Страж процесса: живость watchdog'а проверяем ДО busy-гашения — она не зависит
        // от занятости чата (иначе долгая генерация = слепое пятно по своему стражу).
        _watchdogGuard?.Invoke();

        // FSM: фоновые задачи только в Idle (не во время Generating/Compacting/AwaitingConfirmation/...).
        if (_isBusy())
        {
            return;
        }

        // Flush памяти: рано или поздно все факты векторизуются, не забивая активный поток чата.
        _background.Queue("flush памяти", _flushMemory);

        // Внешний канал: файл в wake/ — событие извне (shell, другой процесс), а не
        // расписание: обрабатываем независимо от HeartbeatEnabled (решение владельца,
        // 2026-10-16: настраиваемое расписание и внешние будильщики разделены).
        if (_wakeSignals?.TakeNext() is { } wake)
        {
            _lastTurnAt = _utcNow();
            _setStatus($"⏰ wake-сигнал: {wake.Source}");
            _background.Queue("wake-ход", () => _startTurn($"[wake:{wake.Source}] {wake.Text}"));
            return;
        }

        // Настраиваемый канал: плановое пробуждение по интервалу.
        if (!_heartbeatEnabled())
        {
            return;
        }

        // UtcNow для интервалов: локальное время прыгает при переводе часов/NTP.
        var now = _utcNow();
        if ((now - _lastTurnAt).TotalMinutes < _intervalMinutes())
        {
            return;
        }

        _lastTurnAt = now;
        _setStatus("⏰ heartbeat");
        _background.Queue("heartbeat-ход", () => _startTurn(DefaultPrompt));
    }

    /// <summary>
    /// Внешний wake с готовым промптом (pigeon-mention и пр.): сразу, без ожидания тика,
    /// независимо от HeartbeatEnabled. Busy-гвард: во время хода не планируем второй вход
    /// (окно «один вход»: бюджет-чех до FSM) — вызыватель сам повторит, когда чат освободится.
    /// </summary>
    public void Wake(string prompt)
    {
        if (_isBusy())
        {
            return;
        }
        _lastTurnAt = _utcNow();
        _setStatus("⏰ внешний wake");
        _background.Queue("внешний wake-ход", () => _startTurn(prompt));
    }

    /// <summary>
    /// Ручной wake (кнопка): queued-сигнал (wake/ файл, если есть) или обычный heartbeat-
    /// промпт; вне расписания, независимо от HeartbeatEnabled; плановый отсчёт сбрасывается.
    /// </summary>
    public void WakeNow()
    {
        // Busy-гвард (как в Tick): во время активного хода wake-ход не планируем.
        // Сигнал НЕ съедаем: следующий тик подхватит.
        if (_isBusy())
        {
            return;
        }
        _lastTurnAt = _utcNow();
        var signal = _wakeSignals?.TakeNext();
        _setStatus(signal is { } s ? $"⏰ wake-сигнал: {s.Source}" : "⏰ ручной wake");
        _background.Queue("ручной wake-ход",
            () => _startTurn(signal is { } wake ? $"[wake:{wake.Source}] {wake.Text}" : DefaultPrompt));
    }
}
