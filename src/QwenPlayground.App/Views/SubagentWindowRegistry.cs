using System.Windows.Input;
using QwenPlayground.App.ViewModels;

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

/// <summary>
/// ChatViewModel текущего субагента (встроенный чат в пузыре tool call): VM создаётся
/// в RunSubagentAsync и живёт, пока субагент жив в процессе (до рестарта). Пузырь
/// (MessageViewModel.SubagentChat) и popout-окно (ChatWindow.CreateWindowFor) ссылаются
/// на один и тот же инстанс — окно закрывается, чат в пузыре остаётся.
/// </summary>
public static class SubagentChatRegistry
{
    private static (string SessionId, ChatViewModel Chat)? _current;

    public static (string SessionId, ChatViewModel Chat)? Current => _current;

    public static void SetCurrent(string sessionId, ChatViewModel chat) => _current = (sessionId, chat);

    public static void Clear() => _current = null;
}
