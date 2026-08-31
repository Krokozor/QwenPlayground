using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using QwenPlayground.Core.Runtime;

namespace QwenPlayground.App;

/// <summary>
/// UI-диспетчер ходов: переводит <see cref="TurnRegistry"/> в коллекцию для списка.
/// Реестр живёт в Core без INPC — панель перестраивает элементы на каждое Changed
/// (ходов единицы, перестройка дешёвая; паттерн ChatSessions.RefreshList).
/// </summary>
public sealed class TurnPanel : INotifyPropertyChanged
{
    public ObservableCollection<TurnPanelItem> Items { get; } = new();

    /// <summary>Строка-сводка для заголовка панели («Ходы: 1 выполняется, 2 готово»).</summary>
    public string Summary => _summary;

    private string _summary = "Ходы";

    private readonly TurnRegistry _registry;

    public TurnPanel(TurnRegistry registry)
    {
        _registry = registry;
        registry.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        Items.Clear();
        var running = 0;
        foreach (var turn in Enumerable.Reverse(_registry.Turns)) // свежие сверху
        {
            if (turn.State is TurnState.Queued or TurnState.Running)
            {
                running++;
            }
            Items.Add(new TurnPanelItem(turn));
        }
        var done = _registry.Turns.Count - running;
        _summary = running > 0
            ? $"Ходы: {running} выполняется · {done} в истории"
            : $"Ходы: {done} в истории";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
    }

    /// <summary>Запросить отмену хода из UI; реестр поднимет Changed — панель обновится сама.</summary>
    public void Cancel(TurnPanelItem item) => _registry.Cancel(item.Turn.Id);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Вид одной строки списка ходов; вычисляется на момент перестройки панели.</summary>
public sealed class TurnPanelItem
{
    public TurnEntry Turn { get; }
    public string Name => Turn.Name;
    public string StateText { get; }
    public Brush StateBrush { get; }
    public string DurationText { get; }
    public string? Details { get; }
    public bool IsRunning => Turn.State is TurnState.Queued or TurnState.Running;

    public TurnPanelItem(TurnEntry turn)
    {
        Turn = turn;
        StateText = turn.State switch
        {
            TurnState.Queued => "в очереди",
            TurnState.Running => "выполняется",
            TurnState.Succeeded => "готово",
            TurnState.Failed => "ошибка",
            TurnState.Canceled => "отменено",
            _ => turn.State.ToString()
        };
        StateBrush = turn.State switch
        {
            TurnState.Queued or TurnState.Running => RunningBrush,
            TurnState.Succeeded => OkBrush,
            TurnState.Failed => WarnBrush,
            _ => DimBrush
        };
        DurationText = FormatDuration(turn);
        Details = BuildDetails(turn);
    }

    // Палитра дома (App.xaml) + акценты состояния как в MemoryView.
    private static readonly Brush RunningBrush = new SolidColorBrush(Color.FromRgb(0x4f, 0xc1, 0xff));
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x6a, 0x99, 0x55));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xd1, 0x69, 0x69));
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x9d, 0x9d, 0x9d));

    private static string FormatDuration(TurnEntry turn) => turn.Duration is { } duration
        ? duration.TotalSeconds < 1
            ? $"{duration.TotalMilliseconds:F0} мс"
            : $"{duration.TotalSeconds:F1} с"
        : "…";

    private static string? BuildDetails(TurnEntry turn)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(turn.Error))
        {
            parts.Add(turn.Error!);
        }
        parts.AddRange(turn.Journal.TakeLast(5));
        return parts.Count == 0 ? null : string.Join(" → ", parts);
    }
}
