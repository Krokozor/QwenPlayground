namespace QwenPlayground.Core.Chat;

/// <summary>
/// Логические состояния чата. FSM: состояния линейны, переходы явные.
/// Зачем: IsGenerating как bool не выражает «генерация с паузой на компакцию»,
/// «ожидание подтверждения действия», «ручная компакция идёт параллельно». FSM убирает реентерабельность.
/// </summary>
public enum ChatState
{
    /// <summary>Идл: нет активного хода, можно принимать новые.</summary>
    Idle,
    /// <summary>Агентный/чат-ход идёт (LLM генерирует, tool-calls выполняются).</summary>
    Generating,
    /// <summary>Компакция контекста (ручная или авто между итерациями).</summary>
    Compacting,
    /// <summary>Ожидание подтверждения (confirm).</summary>
    AwaitingConfirmation,
    /// <summary>Перезапуск в новую версию запрошен (терминальное для текущего процесса).</summary>
    RestartPending
}

/// <summary>
/// Машина состояний чата. Все переходы валидируются по таблице.
/// Потокобезопасность: все переходы должны происходить на dispatcher-потоке WPF
/// (MainViewModel живёт на UI-потоке), поэтому лoki не нужны.
/// </summary>
public sealed class ChatStateMachine
{
    public ChatState Current { get; private set; } = ChatState.Idle;

    /// <summary>Событие смены состояния (для UI и логов).</summary>
    public event Action<ChatState, ChatState>? StateChanged;

    /// <summary>
    /// Таблица разрешённых переходов. Ключ — текущее состояние, значение — множество целевых.
    /// </summary>
    private static readonly Dictionary<ChatState, HashSet<ChatState>> AllowedTransitions = new()
    {
        [ChatState.Idle] = new() { ChatState.Generating, ChatState.Compacting, ChatState.RestartPending },
        [ChatState.Generating] = new() { ChatState.Compacting, ChatState.AwaitingConfirmation, ChatState.RestartPending, ChatState.Idle },
        [ChatState.Compacting] = new() { ChatState.Generating, ChatState.Idle },
        [ChatState.AwaitingConfirmation] = new() { ChatState.Generating },
        [ChatState.RestartPending] = new() { } // терминальное
    };

    public bool CanTransition(ChatState to) =>
        AllowedTransitions.TryGetValue(Current, out var allowed) && allowed.Contains(to);

    /// <summary>
    /// Переход в новое состояние. Бросает InvalidOperationException при недопустимом переходе.
    /// </summary>
    public void Transition(ChatState to)
    {
        if (!TryTransition(to))
        {
            throw new InvalidOperationException(
                $"Недопустимый переход чата: {Current} → {to}. Разрешены: {string.Join(", ", AllowedTransitions[Current])}");
        }
    }

    /// <summary>
    /// Попытка перехода без исключения (для guard-проверок).
    /// </summary>
    public bool TryTransition(ChatState to)
    {
        if (!CanTransition(to))
        {
            return false;
        }
        var from = Current;
        Current = to;
        StateChanged?.Invoke(from, to);
        return true;
    }

    /// <summary>Чат занят (нельзя принимать новые ходы/ручную компакцию).</summary>
    public bool IsBusy => Current is ChatState.Generating or ChatState.Compacting
        or ChatState.AwaitingConfirmation;

    /// <summary>Можно отменить текущий ход (только в Generating).</summary>
    public bool CanCancel => Current == ChatState.Generating;
}
