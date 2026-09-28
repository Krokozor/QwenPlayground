using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Subagents;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Лизинг субагента (фаза 2, план 2026-09-28): ClearCurrent снимает лизинг в конце
/// хода (RunSubagentAsync) — второй спавн не требует рестарта процесса. До фикса
/// ClearCurrent был мёртвым кодом (ноль вызовов) и гвард SpawnAsync срабатывал
/// «до следующего рестарта» после любого спавна.
/// </summary>
public sealed class SubagentSpawnerTests
{
    private static SubagentSpawner NewSpawner() =>
        new(new KvCacheController(), () => "main");

    [Fact]
    public void Lease_ClearCurrent_ResetsGuard_AndIsIdempotent()
    {
        var spawner = NewSpawner();

        spawner.SetCurrent("s1", "title", isRunning: true);
        Assert.NotNull(spawner.Current);
        Assert.True(spawner.Current!.IsRunning);

        spawner.ClearCurrent("s1");
        Assert.Null(spawner.Current);

        // Идемпотентно: повторный вызов (закрытие окна после конца хода) не ломает.
        spawner.ClearCurrent("s1");
        Assert.Null(spawner.Current);
    }

    [Fact]
    public void ClearCurrent_OtherSession_DoesNotTouchCurrent()
    {
        var spawner = NewSpawner();

        spawner.SetCurrent("s1", "title", isRunning: true);
        spawner.ClearCurrent("s2");

        Assert.NotNull(spawner.Current);
        Assert.Equal("s1", spawner.Current!.SessionId);
    }

    [Fact]
    public void SetRunning_ClearsNothing_OnlyUpdatesFlag()
    {
        var spawner = NewSpawner();

        spawner.SetCurrent("s1", "title", isRunning: true);
        spawner.SetRunning(false);

        Assert.NotNull(spawner.Current);
        Assert.False(spawner.Current!.IsRunning);
    }
}
