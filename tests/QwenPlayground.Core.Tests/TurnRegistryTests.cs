using QwenPlayground.Core.Runtime;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Бухгалтерия реестра ходов: жизненный цикл личности (Queued→Running→терминал),
/// журнал, контракт мутаторов, отмена, вытеснение истории.
/// </summary>
public sealed class TurnRegistryTests
{
    private readonly TurnRegistry _registry = new();

    [Fact]
    public void Register_CreatesQueuedEntry_WithSeededJournal()
    {
        var turn = _registry.Register("тест-ход");

        Assert.Equal(TurnState.Queued, turn.State);
        Assert.Equal("тест-ход", turn.Name);
        Assert.Contains("создан", turn.Journal);
        Assert.Null(turn.Duration);
    }

    [Fact]
    public void Lifecycle_Success_TransitionsAndDuration()
    {
        var changed = 0;
        _registry.Changed += () => changed++;
        var turn = _registry.Register("ход");

        turn.Begin();
        turn.Finish(TurnState.Succeeded);

        Assert.Equal(TurnState.Succeeded, turn.State);
        Assert.NotNull(turn.FinishedAt);
        Assert.True(turn.Duration >= TimeSpan.Zero);
        Assert.True(changed >= 3); // регистрация + begin + finish
    }

    [Fact]
    public void Finish_Failed_StoresError()
    {
        var turn = _registry.Register("падающий");
        turn.Begin();

        turn.Finish(TurnState.Failed, "сервер недоступен");

        Assert.Equal(TurnState.Failed, turn.State);
        Assert.Equal("сервер недоступен", turn.Error);
    }

    [Fact]
    public void MutatorContract_BeginFromTerminal_AndDoubleFinish_Throw()
    {
        var turn = _registry.Register("ход");
        turn.Begin();
        turn.Finish(TurnState.Succeeded);

        Assert.Throws<InvalidOperationException>(() => turn.Begin());
        Assert.Throws<InvalidOperationException>(() => turn.Finish(TurnState.Failed));
    }

    [Fact]
    public void Finish_NonTerminalState_Rejected()
    {
        var turn = _registry.Register("ход");

        Assert.Throws<InvalidOperationException>(() => turn.Finish(TurnState.Running));
    }

    [Fact]
    public void Log_AppendsStage()
    {
        var turn = _registry.Register("многоэтапный");
        var seen = 0;
        _registry.Changed += () => seen++;

        turn.Log("компакция");
        turn.Log("стрим 2/5");

        Assert.Contains("компакция", turn.Journal);
        Assert.Contains("стрим 2/5", turn.Journal);
        Assert.True(seen >= 2);
    }

    [Fact]
    public async Task Cancel_RaisesToken_OnActiveTurn()
    {
        var turn = _registry.Register("долгий");
        turn.Begin();

        Assert.True(_registry.Cancel(turn.Id));
        Assert.True(turn.Cancellation.Token.IsCancellationRequested);
        // Отмена фиксируется в журнале для UI
        Assert.Contains("запрошена отмена", turn.Journal);

        await Task.CompletedTask;
    }

    [Fact]
    public void Cancel_TerminalOrUnknown_ReturnsFalse()
    {
        var turn = _registry.Register("закрытый");
        turn.Begin();
        turn.Finish(TurnState.Canceled);

        Assert.False(_registry.Cancel(turn.Id));
        Assert.False(_registry.Cancel(Guid.NewGuid()));
    }

    [Fact]
    public void HistoryCap_TrimsOldestFinished_KeepsActive()
    {
        var registry = new TurnRegistry { HistoryLimit = 2 };
        var first = registry.Register("старейший");
        first.Begin();
        first.Finish(TurnState.Succeeded);
        foreach (var name in new[] { "b", "c", "d" })
        {
            var t = registry.Register(name);
            t.Begin();
            t.Finish(TurnState.Succeeded);
        }
        var active = registry.Register("живой"); // Queued — не вытесняется

        var names = registry.Turns.Select(t => t.Name).ToList();

        Assert.DoesNotContain("старейший", names); // вытеснен первым
        Assert.Contains("живой", names);
        Assert.True(registry.Turns.Count(t => t.State == TurnState.Succeeded) <= 2 + 1); // b,c,d → максимум 3 до следующей чистки
    }
}
