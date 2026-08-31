using QwenPlayground.Core.Memory;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Тестируемая часть MemorySurfacer — наг менеджмента памяти (чистая логика счётчика).
/// Реколл (RecallAfterTurnAsync/RecallLiveAsync) ходит на компаньон-модели и покрывается
/// смоуком (harness), а не юнит-тестом.
/// </summary>
public sealed class MemorySurfacerTests
{
    [Fact]
    public void MemoryNag_IsNull_BeforeInterval()
    {
        var surfacer = new MemorySurfacer();

        for (var i = 0; i < 14; i++)
        {
            surfacer.OnRendered();
        }

        Assert.Null(surfacer.MemoryNag);
    }

    [Fact]
    public void MemoryNag_Fires_AtInterval()
    {
        var surfacer = new MemorySurfacer();

        for (var i = 0; i < 15; i++)
        {
            surfacer.OnRendered();
        }

        var nag = surfacer.MemoryNag;
        Assert.NotNull(nag);
        Assert.Contains("memory_list", nag);
    }

    [Fact]
    public void MemoryNag_Resets_OnMemoryToolUsed()
    {
        var surfacer = new MemorySurfacer();
        for (var i = 0; i < 15; i++)
        {
            surfacer.OnRendered();
        }
        Assert.NotNull(surfacer.MemoryNag);

        surfacer.OnMemoryToolUsed();

        Assert.Null(surfacer.MemoryNag);
    }

    [Fact]
    public void MemoryNag_CountsFromReset()
    {
        var surfacer = new MemorySurfacer();
        for (var i = 0; i < 10; i++)
        {
            surfacer.OnRendered();
        }
        surfacer.OnMemoryToolUsed(); // сброс на 0
        for (var i = 0; i < 14; i++)
        {
            surfacer.OnRendered();
        }

        Assert.Null(surfacer.MemoryNag); // 14 после сброса — ещё не 15
    }
}
