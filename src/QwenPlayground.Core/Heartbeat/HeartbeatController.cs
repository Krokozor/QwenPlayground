using QwenPlayground.Core.Runtime;

namespace QwenPlayground.Core.Heartbeat;

/// <summary>
/// Сердцебиение приложения: на каждом тике опрашивается wake/ (сигналы не ждут расписания),
/// по истечении интервала планируется плановое пробуждение. Исполнение хода и flush памяти
/// остаются снаружи (нужны FSM, endpoint, настройки) — контроллер владеет только решением
/// «когда и чем разбудить» и статусами. Без таймера: качает UI (DispatcherTimer) или тест
/// (Tick() напрямую); часы подменяются через <paramref name="clock"/>.
/// </summary>
public sealed class HeartbeatController
{
    public const string DefaultPrompt =
        "[heartbeat] Periodic autonomous wake-up. Check refactoring.md for pending work and decide if anything " +
        "small and safe is worth doing now. If you find something concrete (bug, unfinished item, cleanup), do it " +
        "with your tools and keep the change focused; if you modified application code, finish with rebuild_self. " +
        "If nothing needs attention, reply with a one-line status and stop. Do not start large refactors or risky changes unprompted.";

    private readonly WakeSignalStore _wakeSignals;
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
        WakeSignalStore wakeSignals,
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

    /// <summary>Один тик опроса: busy гасит всё, flush памяти бежит всегда при свободном чате.</summary>
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

        var prompt = ResolveScheduledTurn();
        if (prompt is not null)
        {
            _background.Queue("heartbeat-ход", () => _startTurn(prompt));
        }
    }

    /// <summary>
    /// Ручной wake (кнопка): сигнал или обычный heartbeat вне расписания; плановый отсчёт сбрасывается.
    /// </summary>
    public void WakeNow()
    {
        _lastTurnAt = _utcNow();
        var signal = _wakeSignals.TakeNext();
        _setStatus("⏰ ручной wake");
        _background.Queue("ручной wake-ход",
            () => _startTurn(signal is { } wake ? $"[wake:{wake.Source}] {wake.Text}" : DefaultPrompt));
    }

    /// <summary>
    /// Решение планового пробуждения: wake-сигнал важнее расписания (и сбрасывает его отсчёт
    /// фактом хода). Возвращает текст промпта или null — ход не нужен.
    /// </summary>
    private string? ResolveScheduledTurn()
    {
        if (!_heartbeatEnabled())
        {
            return null;
        }

        var signal = _wakeSignals.TakeNext();
        if (signal is { } wake)
        {
            _lastTurnAt = _utcNow();
            _setStatus($"⏰ wake-сигнал: {wake.Source}");
            return $"[wake:{wake.Source}] {wake.Text}";
        }

        // UtcNow для интервалов: локальное время прыгает при переводе часов/NTP.
        var now = _utcNow();
        if ((now - _lastTurnAt).TotalMinutes < _intervalMinutes())
        {
            return null;
        }

        _lastTurnAt = now;
        _setStatus("⏰ heartbeat");
        return DefaultPrompt;
    }
}
