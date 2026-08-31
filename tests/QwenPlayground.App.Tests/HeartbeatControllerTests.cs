using System.IO;
using QwenPlayground.App;
using QwenPlayground.Core.Heartbeat;

namespace QwenPlayground.App.Tests;

/// <summary>
/// Логика сердцебиения без таймера: Tick()/WakeNow() дёргаются напрямую, часы и все
/// зависимости инжектируются. WakeSignalStore — реальный, во временном каталоге.
/// </summary>
public sealed class HeartbeatControllerTests : IDisposable
{
    private readonly string _wakeDir = Path.Combine(Path.GetTempPath(), "qpw_hb_" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _start = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);

    private DateTime _now;
    private readonly List<string> _turns = [];
    private readonly List<string> _statuses = [];
    private readonly List<string> _backgroundErrors = [];
    private int _flushCount;
    private bool _busy;
    private bool _enabled = true;
    private double _intervalMinutes = 30;

    public HeartbeatControllerTests()
    {
        _now = _start;
    }

    private HeartbeatController Create(WakeSignalStore? signals = null, Func<string, Task>? startTurn = null)
    {
        return new HeartbeatController(
            signals ?? new WakeSignalStore(_wakeDir),
            isBusy: () => _busy,
            heartbeatEnabled: () => _enabled,
            heartbeatIntervalMinutes: () => _intervalMinutes,
            setStatus: s => _statuses.Add(s),
            startTurn: startTurn ?? (prompt => { _turns.Add(prompt); return Task.CompletedTask; }),
            flushMemory: () => { _flushCount++; return Task.CompletedTask; },
            timer: null,
            clock: () => _now,
            background: new BackgroundWork(s => _backgroundErrors.Add(s)));
    }

    private void SendWake(string text) => new WakeSignalStore(_wakeDir).Send(text);

    [Fact]
    public void Busy_NoFlushAndNoTurn()
    {
        var hb = Create();
        SendWake("задача");
        _busy = true;

        hb.Tick();

        Assert.Equal(0, _flushCount);
        Assert.Empty(_turns);
    }

    [Fact]
    public void IdleTick_RunsMemoryFlush_EvenWhenHeartbeatDisabled()
    {
        _enabled = false;
        var hb = Create();

        hb.Tick();

        Assert.Equal(1, _flushCount);
        Assert.Empty(_turns);
    }

    [Fact]
    public void WakeSignal_FiresTurnImmediately_AndBeatsSchedule()
    {
        var hb = Create();
        SendWake("проверь тесты");

        hb.Tick();

        var turn = Assert.Single(_turns);
        Assert.Contains("[wake:", turn);
        Assert.Contains("проверь тесты", turn);
        Assert.StartsWith("⏰ wake-сигнал:", _statuses[0]);
    }

    [Fact]
    public void IntervalNotElapsed_NoScheduledTurn()
    {
        var hb = Create(); // отсчёт от _start

        _now = _start.AddMinutes(29);
        hb.Tick();

        Assert.Empty(_turns);
    }

    [Fact]
    public void IntervalElapsed_ScheduledFires_ThenWaitsAgain()
    {
        var hb = Create();

        _now = _start.AddMinutes(31);
        hb.Tick();
        Assert.Equal(HeartbeatController.DefaultPrompt, Assert.Single(_turns));
        Assert.Contains("⏰ heartbeat", _statuses);

        // Сразу после пробуждения повторный тик не будит второй раз.
        hb.Tick();
        Assert.Single(_turns);
    }

    [Fact]
    public void Disabled_NoScheduledTurn_AndSignalsStayQueued()
    {
        var hb = Create();
        SendWake("отложено");
        _enabled = false;

        hb.Tick();
        Assert.Empty(_turns);
        Assert.Equal(1, new WakeSignalStore(_wakeDir).Count); // сигнал не съеден

        _enabled = true;
        hb.Tick();
        Assert.Single(_turns);
    }

    [Fact]
    public void WakeNow_TakesSignalOrFallback_AndResetsSchedule()
    {
        var hb = Create();
        SendWake("ручная задача");

        hb.WakeNow();
        Assert.Contains("ручная задача", Assert.Single(_turns));

        // Без сигнала — дефолтный промпт; расписание сброшено: тик через минуту не будит.
        _now = _start.AddMinutes(1);
        hb.WakeNow();
        Assert.Equal(2, _turns.Count);
        Assert.Equal(HeartbeatController.DefaultPrompt, _turns[1]);

        _now = _start.AddMinutes(2);
        hb.Tick();
        Assert.Equal(2, _turns.Count);
    }

    [Fact]
    public void TurnFailure_IsReported_NotSilent()
    {
        // Падение хода не должно умирать в невидимом таске: владелец фоновой работы
        // докладывает об ошибке (исключение из faulted task обрабатывается синхронно).
        var hb = Create(startTurn: _ => Task.FromException(new InvalidOperationException("сервер упал")));
        _now = _start.AddMinutes(31); // интервал истёк — тик планирует ход

        hb.Tick();

        var error = Assert.Single(_backgroundErrors);
        Assert.Contains("heartbeat-ход", error);
        Assert.Contains("сервер упал", error);
    }

    [Fact]
    public void FlushFailure_IsReported_NotSilent()
    {
        var failures = 0;
        var hb = Create();
        // Подменяем flush через отдельный контроллер: фабрика возвращает падающую задачу.
        var controller = new HeartbeatController(
            new WakeSignalStore(_wakeDir),
            isBusy: () => false,
            heartbeatEnabled: () => true,
            heartbeatIntervalMinutes: () => 30,
            setStatus: _ => { },
            startTurn: _ => Task.CompletedTask,
            flushMemory: () => { failures++; return Task.FromException(new IOException("диск полон")); },
            timer: null,
            clock: () => _now,
            background: new BackgroundWork(s => _backgroundErrors.Add(s)));

        controller.Tick();

        Assert.Equal(1, failures);
        var error = Assert.Single(_backgroundErrors);
        Assert.Contains("flush памяти", error);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_wakeDir))
            {
                Directory.Delete(_wakeDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
