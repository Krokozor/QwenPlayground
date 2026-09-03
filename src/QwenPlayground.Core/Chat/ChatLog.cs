namespace QwenPlayground.Core.Chat;

/// <summary>
/// Владелец разговора: список сообщений + монотонный счётчик стабильных ID.
/// Единственная точка мутации для ViewModel, агентного цикла и компакции — инварианты
/// (присвоение ID, события для синхронизации вида) enforcement'ятся самим типом.
///
/// События — для владельца-экрана: <see cref="Added"/> после добавления с уже присвоенным
/// ID, <see cref="Changed"/> на структурные изменения (очистка/замена/обрезка). Домен
/// ничего не знает о виде. Всё исполняется на главном потоке (инвариант проекта) —
/// без блокировок; чтение во время стрима безопасно (список мутируется только с хвоста).
/// </summary>
public sealed class ChatLog : IReadOnlyList<ChatMessage>
{
    private readonly List<ChatMessage> _messages = new();
    private int _nextMessageId = 1;

    /// <summary>Добавлено сообщение (ID уже присвоен) — владелец досоздаёт вид при необходимости.</summary>
    public event Action<ChatMessage>? Added;

    /// <summary>Структурное изменение, кроме одиночного добавления: очистка/замена/обрезка хвоста.</summary>
    public event Action? Changed;

    public int Count => _messages.Count;
    public ChatMessage this[int index] => _messages[index];
    public int NextMessageId => _nextMessageId;

    public IEnumerator<ChatMessage> GetEnumerator() => _messages.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Счётчик из персистентной сессии (SessionData.NextMessageId); только вперёд.</summary>
    public void SetNextMessageId(int value)
    {
        if (value > _nextMessageId)
        {
            _nextMessageId = value;
        }
    }

    /// <summary>
    /// Добавить сообщение. System получает Id=0; без Id — следующий стабильный;
    /// уже нумерованное — сдвигает счётчик выше себя (ID никогда не переиспользуются).
    /// </summary>
    public void Add(ChatMessage message)
    {
        AssignId(message);
        _messages.Add(message);
        Added?.Invoke(message);
    }

    /// <summary>Очистить разговор.</summary>
    public void Clear()
    {
        _messages.Clear();
        Changed?.Invoke();
    }

    /// <summary>Заменить разговор целиком (загрузка сессии, компакция ветки). Одно событие Changed.</summary>
    public void ReplaceAll(IEnumerable<ChatMessage> messages)
    {
        _messages.Clear();
        foreach (var message in messages)
        {
            AssignId(message);
            _messages.Add(message);
        }
        Changed?.Invoke();
    }

    /// <summary>Защищённая копия диапазона (сегменты для компакции/транскриптов).</summary>
    public List<ChatMessage> CopyRange(int index, int count)
    {
        var copy = new List<ChatMessage>(Math.Max(0, count));
        for (var i = index; i < index + count && i < _messages.Count; i++)
        {
            copy.Add(_messages[i]);
        }
        return copy;
    }

    /// <summary>Обрезать хвост, оставив первые <paramref name="keepCount"/> сообщений (компакция main).</summary>
    public void TruncateKeep(int keepCount)
    {
        if (keepCount >= _messages.Count)
        {
            return;
        }
        _messages.RemoveRange(keepCount, _messages.Count - keepCount);
        Changed?.Invoke();
    }

    /// <summary>
    /// Компакция: ранняя часть (после ведущего system-сообщения и до <paramref name="boundary"/>)
    /// уже дистиллирована в слои — удаляем её, оставляя system (если есть) и хвост с границы.
    /// <paramref name="boundary"/> — индекс, с которого начинается сохраняемый хвост.
    /// ID удалённых сообщений не переиспользуются (счётчик только вперёд).
    /// </summary>
    public void TrimCompactedPrefix(int boundary)
    {
        var systemEnd = _messages.Count > 0 && _messages[0].Role == ChatRole.System ? 1 : 0;
        if (boundary <= systemEnd || boundary >= _messages.Count)
        {
            return;
        }
        _messages.RemoveRange(systemEnd, boundary - systemEnd);
        Changed?.Invoke();
    }

    /// <summary>Удалить хвост начиная с index включительно (откат к сообщению).</summary>
    public void RemoveFrom(int index)
    {
        if (index >= _messages.Count)
        {
            return;
        }
        _messages.RemoveRange(index, _messages.Count - index);
        Changed?.Invoke();
    }

    /// <summary>
    /// Прогнать нумерацию по всему списку: догоняет сообщения, добавленные мимо Add
    /// (старые сессии без ID). Вызывается перед каждым рендером state-блока и сохранением.
    /// </summary>
    public void AssignPendingIds()
    {
        foreach (var message in _messages)
        {
            AssignId(message);
        }
    }

    private void AssignId(ChatMessage message)
    {
        if (message.Role == ChatRole.System)
        {
            message.Id = 0;
            return;
        }
        if (message.Id == 0)
        {
            message.Id = _nextMessageId++;
        }
        else
        {
            _nextMessageId = Math.Max(_nextMessageId, message.Id + 1);
        }
    }
}
