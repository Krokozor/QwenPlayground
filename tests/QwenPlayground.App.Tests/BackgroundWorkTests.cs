using QwenPlayground.Core.Runtime;
using QwenPlayground.App;

namespace QwenPlayground.App.Tests;

/// <summary>
/// Политика присмотра BackgroundWork поверх реестра ходов: исключение → Failed + отчёт,
/// OCE → Canceled без отчёта, отмена доходит до работы токеном.
/// </summary>
public sealed class BackgroundWorkTests
{
    private readonly List<string> _reports = new();
    private readonly TurnRegistry _registry = new();
    private readonly BackgroundWork _background;

    public BackgroundWorkTests() => _background = new BackgroundWork(_reports.Add, _registry);

    [Fact]
    public async Task Success_NoReport_TurnSucceeded()
    {
        await _background.RunAsync("работа", _ => Task.CompletedTask);

        Assert.Empty(_reports);
        var turn = Assert.Single(_registry.Turns);
        Assert.Equal(TurnState.Succeeded, turn.State);
        Assert.Null(turn.Error);
    }

    [Fact]
    public async Task Exception_ReportedWithName_TurnFailed()
    {
        await _background.RunAsync("падающая работа", _ => throw new InvalidOperationException("бум"));

        var report = Assert.Single(_reports);
        Assert.Contains("падающая работа", report);
        Assert.Contains("бум", report);
        var turn = Assert.Single(_registry.Turns);
        Assert.Equal(TurnState.Failed, turn.State);
        Assert.Equal("бум", turn.Error);
    }

    [Fact]
    public async Task OperationCanceled_TurnCanceled_NoReport()
    {
        await _background.RunAsync("отменённая", _ => throw new OperationCanceledException());

        Assert.Empty(_reports);
        var turn = Assert.Single(_registry.Turns);
        Assert.Equal(TurnState.Canceled, turn.State);
    }

    [Fact]
    public async Task Queue_FuncTaskOverload_Completes()
    {
        var done = new TaskCompletionSource();
        _background.Queue("классический вызов", () => { done.SetResult(); return Task.CompletedTask; });

        // fire-and-forget: завершение работы отслеживаем её собственным сигналом
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cancellation_TokenReachesWork_AndTurnCanceled()
    {
        // Работа запрашивает отмену через РЕЕСТР (как кнопка UI) и видит токен хода.
        await _background.RunAsync("отменяемая изнутри", ct =>
        {
            _registry.Cancel(_registry.Turns.Single().Id);
            return Task.Delay(Timeout.Infinite, ct);
        });

        var turn = Assert.Single(_registry.Turns);
        Assert.Equal(TurnState.Canceled, turn.State);
        Assert.Contains("запрошена отмена", turn.Journal);
        Assert.Empty(_reports); // отмена — штатный путь
    }

    [Fact]
    public async Task TurnPanel_ReflectsRegistryStates()
    {
        var panel = new TurnPanel(_registry);

        await _background.RunAsync("видимая работа", _ => Task.CompletedTask);

        panel.Refresh();
        var item = Assert.Single(panel.Items);
        Assert.Equal("видимая работа", item.Name);
        Assert.Equal("готово", item.StateText);
        Assert.False(item.IsRunning);
        Assert.StartsWith("Ходы:", panel.Summary);
    }
}
