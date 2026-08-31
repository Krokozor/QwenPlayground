namespace QwenPlayground.Core.Runtime;

/// <summary>
/// Реестр ходов приложения: каждый фоновый/агентный ход получает личность
/// (<see cref="TurnEntry"/>: id, имя, состояние, журнал, отмена) вместо растворения
/// в невидимом Task. UI-диспетчер подписывается на <see cref="Changed"/> и рисует
/// список ходов; будущий оркестратор регистрирует здесь дочерние ходы так же.
///
/// Исполнитель один (BackgroundWork), конкурентности нет — FSM и гварды сериализуют
/// работу на главном потоке (инвариант проекта), поэтому список без блокировок.
/// </summary>
public sealed class TurnRegistry
{
    private readonly List<TurnEntry> _turns = new();

    /// <summary>Активные и завершённые ходы, новые в конце; история обрезается лимитом.</summary>
    public IReadOnlyList<TurnEntry> Turns => _turns;

    /// <summary>Сколько ЗАВЕРШЁННЫХ ходов держать в истории (активные не вытесняются).</summary>
    public int HistoryLimit { get; init; } = 50;

    /// <summary>Любое изменение любого хода (переход состояния, запись в журнал).</summary>
    public event Action? Changed;

    /// <summary>Зарегистрировать новый ход (Queued) и вернуть его личность исполнителю.</summary>
    public TurnEntry Register(string name)
    {
        var entry = new TurnEntry(name);
        entry.Attach(() => Changed?.Invoke());
        _turns.Add(entry);
        TrimHistory();
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Найти ход по идентификатору (для отмены из UI). null — нет такого.</summary>
    public TurnEntry? Find(Guid id) => _turns.FirstOrDefault(t => t.Id == id);

    /// <summary>Запросить отмену хода; true — ход был активен и токен поднят.</summary>
    public bool Cancel(Guid id)
    {
        var turn = Find(id);
        if (turn is null || turn.State is TurnState.Succeeded or TurnState.Failed or TurnState.Canceled)
        {
            return false;
        }
        turn.Log("запрошена отмена");
        turn.Cancel();
        return true;
    }

    /// <summary>Вытеснение старых завершённых ходов за лимитом истории.</summary>
    private void TrimHistory()
    {
        var finished = _turns.Where(t =>
            t.State is TurnState.Succeeded or TurnState.Failed or TurnState.Canceled).ToList();
        var excess = finished.Count - HistoryLimit;
        foreach (var stale in finished.Take(Math.Max(0, excess)))
        {
            _turns.Remove(stale);
        }
    }
}
