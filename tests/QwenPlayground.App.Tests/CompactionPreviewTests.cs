using QwenPlayground.App.ViewModels;

namespace QwenPlayground.App.Tests;

/// <summary>
/// Троттлинг-публикация завязана на реальный Stopwatch (порог 50 мс) — тесты используют
/// короткие Sleep'ы. Это делает их чуть медленными, но честными относительно таймингов UI.
/// </summary>
public sealed class CompactionPreviewTests
{
    private static void WaitThrottleWindow() => Thread.Sleep(60);

    [Fact]
    public void Begin_ResetsState_AndActivatesPanel()
    {
        var preview = new CompactionPreview();
        preview.Append("старое");
        WaitThrottleWindow();
        preview.Append(" новое");
        preview.End();

        preview.Begin();

        Assert.True(preview.IsActive);
        Assert.True(preview.ShowPanel);
        Assert.Equal(string.Empty, preview.Preview);
        Assert.Equal(string.Empty, preview.Stage);
    }

    [Fact]
    public void Append_PublishesAfterThrottleWindow()
    {
        var preview = new CompactionPreview();
        preview.Begin();

        // Сразу после Begin порог не истёк — публикация отложена.
        preview.Append("часть1");
        Assert.NotEqual("часть1", preview.Preview);

        WaitThrottleWindow();
        preview.Append("часть2");
        Assert.Equal("часть1часть2", preview.Preview);
    }

    [Fact]
    public void NewStage_AppendsSeparator_AndSetsStage()
    {
        var preview = new CompactionPreview();
        preview.Begin();
        WaitThrottleWindow();

        preview.NewStage("слияние слоёв");

        Assert.Equal("слияние слоёв", preview.Stage);
        Assert.Contains("── слияние слоёв ──", preview.Preview);
    }

    [Fact]
    public void Flush_ForcesPublication_EvenInsideThrottleWindow()
    {
        var preview = new CompactionPreview();
        preview.Begin();
        preview.Append("финальный хвост");

        preview.Flush();

        Assert.Equal("финальный хвост", preview.Preview);
    }

    [Fact]
    public void End_Deactivates_ButKeepsPreviewVisible()
    {
        var preview = new CompactionPreview();
        preview.Begin();
        WaitThrottleWindow();
        preview.Append("итог");
        preview.Flush();
        preview.End();

        Assert.False(preview.IsActive);
        Assert.True(preview.HasPreview);
        Assert.True(preview.ShowPanel); // превью прошлого прогона остаётся на экране
        Assert.Equal("итог", preview.Preview);
    }

    [Fact]
    public void Hide_ClosesPanel_ButKeepsPreview()
    {
        var preview = new CompactionPreview();
        preview.Begin();
        WaitThrottleWindow();
        preview.Append("итог");
        preview.Flush();
        preview.End();

        preview.Hide();

        Assert.True(preview.IsHidden);
        Assert.False(preview.ShowPanel); // «×» убрал панель
        Assert.True(preview.HasPreview); // но превью не потеряно
        Assert.True(preview.HasContent); // кнопка «Сжатие» в тулбаре остаётся активной
    }

    [Fact]
    public void Open_RestoresHiddenPanel()
    {
        var preview = new CompactionPreview();
        preview.Begin();
        WaitThrottleWindow();
        preview.Append("итог");
        preview.Flush();
        preview.End();
        preview.Hide();

        preview.Open();

        Assert.False(preview.IsHidden);
        Assert.True(preview.ShowPanel);
    }

    [Fact]
    public void Begin_ReopensPanel_AfterUserHiddenIt()
    {
        // Авто-открытие при старте компакции сохраняется: «×» не переживает новый прогон.
        var preview = new CompactionPreview();
        preview.Begin();
        WaitThrottleWindow();
        preview.Append("итог");
        preview.Flush();
        preview.End();
        preview.Hide();

        preview.Begin();

        Assert.False(preview.IsHidden);
        Assert.True(preview.ShowPanel);
    }

    [Fact]
    public void HasContent_False_WhenNothingHappened()
    {
        var preview = new CompactionPreview();

        Assert.False(preview.HasContent); // кнопка «Сжатие» в тулбаре неактивна
        Assert.False(preview.ShowPanel);
    }
}
