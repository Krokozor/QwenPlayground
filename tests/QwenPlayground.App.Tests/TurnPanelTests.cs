using QwenPlayground.Core.Runtime;

namespace QwenPlayground.App.Tests;

/// <summary>
/// TurnPanel — UI-стекло над реестром ходов (Core): проверяем, что панель отражает
/// состояния реестра (UI-тест, живёт в App.Tests).
/// </summary>
public sealed class TurnPanelTests
{
    [Fact]
    public async Task TurnPanel_ReflectsRegistryStates()
    {
        var registry = new TurnRegistry();
        var panel = new TurnPanel(registry);
        var background = new BackgroundWork(_ => { }, registry);

        await background.RunAsync("видимая работа", _ => Task.CompletedTask);

        panel.Refresh();
        var item = Assert.Single(panel.Items);
        Assert.Equal("видимая работа", item.Name);
        Assert.Equal("готово", item.StateText);
        Assert.False(item.IsRunning);
        Assert.StartsWith("Ходы:", panel.Summary);
    }
}
