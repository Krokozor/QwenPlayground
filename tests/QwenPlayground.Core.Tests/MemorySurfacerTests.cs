using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Settings;

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
        AppSettings.Update(s => s.MemoryNagEnabled = true);
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
        AppSettings.Update(s => s.MemoryNagEnabled = true);
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
        AppSettings.Update(s => s.MemoryNagEnabled = true);
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
        AppSettings.Update(s => s.MemoryNagEnabled = true);
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

    [Fact]
    public void MemoryNag_IsNull_WhenDisabled()
    {
        AppSettings.Update(s => s.MemoryNagEnabled = false);
        var surfacer = new MemorySurfacer();
        for (var i = 0; i < 15; i++)
        {
            surfacer.OnRendered();
        }

        Assert.Null(surfacer.MemoryNag); // выключено — нага нет даже после интервала
    }

    [Fact]
    public void GetAnnounces_ReturnsMemoryLabeledAnnounce_WhenNagActive()
    {
        AppSettings.Update(s => s.MemoryNagEnabled = true);
        var surfacer = new MemorySurfacer();
        for (var i = 0; i < 15; i++)
        {
            surfacer.OnRendered();
        }

        var announce = Assert.Single(surfacer.GetAnnounces());

        Assert.Equal("memory", announce.Label);
        Assert.Contains("memory_list", Assert.Single(announce.Messages));
    }

    [Fact]
    public void GetAnnounces_Empty_WhenNagInactive()
    {
        var surfacer = new MemorySurfacer();

        Assert.Empty(surfacer.GetAnnounces()); // счётчик 0 — нага нет, анонсов нет
    }

    [Fact]
    public void MemoryNag_IsNull_WhenMemoryDisabled()
    {
        var prev = AppSettings.Get().MemoryEnabled;
        AppSettings.Update(s => { s.MemoryNagEnabled = true; s.MemoryEnabled = false; });
        try
        {
            var surfacer = new MemorySurfacer();
            for (var i = 0; i < 15; i++)
            {
                surfacer.OnRendered();
            }

            Assert.Null(surfacer.MemoryNag); // мастер памяти выкл — нага нет
        }
        finally
        {
            // Тесты параллельны внутри класса, а настройки — статичный синглтон:
            // возвращаем мастер памяти, чтобы соседние тесты не увидели выключенную память.
            AppSettings.Update(s => s.MemoryEnabled = prev);
        }
    }
}
