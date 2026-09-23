using System.Windows.Input;

namespace QwenPlayground.App.Views;

/// <summary>
/// «Открыть окно субагента» для кнопок UI (тулбар, пузырь tool call): держатель делегата,
/// который знает, что делать в обоих случаях — окно живо (Show + Activate) или закрыто
/// (reopening на той же сессии). Ставится MainViewModel'ом один раз; смена вызывает
/// CommandManager.InvalidateRequerySuggested, чтобы CanExecute кнопок перечитался.
/// Кнопка видна, пока субагент существует в процессе (SubagentSpawner.Current) — закрытие
/// окна крестиком его не убивает: сессия на диске, окно можно открыть заново.
/// </summary>
public static class SubagentWindowRegistry
{
    private static Action? _opener;

    public static bool HasOpener => _opener is not null;

    public static void SetOpener(Action opener)
    {
        _opener = opener;
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Открыть окно субагента (или заново создать на его сессии). No-op, если субагента нет.</summary>
    public static void OpenCurrent() => _opener?.Invoke();
}
