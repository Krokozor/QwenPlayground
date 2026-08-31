namespace QwenPlayground.Core.Runtime;

/// <summary>
/// Личность одного фонового/агентного хода (аналог RequestBase из NekoBot): стабильный
/// идентификатор, имя, состояние, журнал этапов и токен отмены. Создаётся реестром
/// (<see cref="TurnRegistry.Register"/>); мутаторы — контракт ИСПОЛНИТЕЛЯ хода
/// (BackgroundWork), бизнес-код состояния не меняет.
///
/// Всё исполняется на главном потоке (инвариант проекта) — без блокировок.
/// </summary>
public sealed class TurnEntry
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Человекочитаемое имя («heartbeat-ход», «flush памяти») — для UI-списка.</summary>
    public string Name { get; }

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

    public TurnState State { get; private set; } = TurnState.Queued;

    /// <summary>Сообщение исключения при Failed; null у остальных состояний.</summary>
    public string? Error { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Журнал этапов многоэтапного хода; исполнители дописывают через <see cref="Log"/>.</summary>
    public IReadOnlyList<string> Journal => _journal;

    private readonly List<string> _journal = new();

    /// <summary>Токен отмены хода: исполнитель передаёт работе, Cancel() дёргается извне.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    // Обратная связь в реестр: переходы состояния поднимают Changed для UI.
    private Action? _onChanged;

    public TurnEntry(string name)
    {
        Name = name;
        Log("создан");
    }

    public TimeSpan? Duration => FinishedAt - CreatedAt;

    /// <summary>Перевод в Running. Повторный вызов/вызов из терминального — ошибка контракта.</summary>
    public void Begin()
    {
        if (State != TurnState.Queued)
        {
            throw new InvalidOperationException($"turn {Name}: Begin() from {State}");
        }
        State = TurnState.Running;
        Changed();
    }

    /// <summary>Терминальное состояние; error значим только для Failed.</summary>
    public void Finish(TurnState state, string? error = null)
    {
        if (State is not (TurnState.Queued or TurnState.Running))
        {
            throw new InvalidOperationException($"turn {Name}: Finish({state}) from {State}");
        }
        if (state is not (TurnState.Succeeded or TurnState.Failed or TurnState.Canceled))
        {
            throw new InvalidOperationException($"turn {Name}: terminal state expected, got {state}");
        }
        State = state;
        Error = error;
        FinishedAt = DateTimeOffset.Now;
        Changed();
    }

    /// <summary>Дописать этап в журнал (например, «компакция», «стрим 2/5»).</summary>
    public void Log(string stage)
    {
        _journal.Add(stage);
        Changed();
    }

    /// <summary>Запросить отмену хода; работа увидит токен на следующем await.</summary>
    public void Cancel() => Cancellation.Cancel();

    /// <summary>Реестр привязывает событие при регистрации; повторно не вызывается.</summary>
    internal void Attach(Action onChanged) => _onChanged = onChanged;

    private void Changed() => _onChanged?.Invoke();
}
