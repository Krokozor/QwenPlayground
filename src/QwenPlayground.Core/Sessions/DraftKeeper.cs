namespace QwenPlayground.Core.Sessions;

/// <summary>
/// Хранитель драфта окошка ввода: периодически (интервал из настроек) проверяет, не
/// изменился ли текст, и сохраняет его в sessions/&lt;id&gt;/draft.txt. Цель — пережить
/// сбой (обрыв питания, крах) и не потерять набранный промпт: при следующем запуске
/// драфт восстанавливается в окошко.
///
/// Правила:
///  - текст непустой и изменился → сохранить в драфт ТЕКУЩЕЙ сессии;
///  - текст стал пустым (был непустым) → удалить драфт (пользователь очистил/отправил);
///  - смена сессии → сначала выгрести текущий текст в драфт СТАРОЙ сессии (Flush), потом
///    восстановить драфт НОВОЙ в окошко (Restore); у каждой сессии свой черновик;
///  - отправка → удалить драфт (текст стал сообщением);
///  - закрытие → выгрести текст (Flush).
///
/// Логика тика — в публичных методах (Tick/Flush/Restore/ClearOnSend), тестируется без
/// таймера. Без таймера: качает UI (DispatcherTimer с интервалом из настроек); свойство
/// <see cref="IntervalSeconds"/> перечитывается UI на каждом тике, поэтому смена в
/// настройках действует без рестарта.
/// </summary>
public sealed class DraftKeeper
{
    private readonly Func<string> _input;
    private readonly Action<string> _setInput;
    private readonly Func<string> _currentSessionId;
    private readonly SessionDraftStore _store;
    private readonly Func<int> _intervalSeconds;

    /// <summary>Последний сохранённый в драфт текст (чтобы не переписывать файл без изменений).</summary>
    private string _lastSaved = string.Empty;

    public DraftKeeper(
        Func<string> input,
        Action<string> setInput,
        Func<string> currentSessionId,
        SessionDraftStore store,
        Func<int> intervalSeconds)
    {
        _input = input;
        _setInput = setInput;
        _currentSessionId = currentSessionId;
        _store = store;
        _intervalSeconds = intervalSeconds;
    }

    /// <summary>Интервал автосохранения (сек) — UI читает на каждом тике таймера.</summary>
    public int IntervalSeconds => Math.Max(1, _intervalSeconds());

    /// <summary>Один тик: сохранить при изменении, удалить при переходе в пустое.</summary>
    public void Tick()
    {
        var text = _input();
        if (text.Length == 0)
        {
            if (_lastSaved.Length > 0)
            {
                _store.Clear(_currentSessionId());
                _lastSaved = string.Empty;
            }
            return;
        }
        if (text != _lastSaved)
        {
            _store.Save(_currentSessionId(), text);
            _lastSaved = text;
        }
    }

    /// <summary>
    /// Выгрести текущий текст в драфт ТЕКУЩЕЙ сессии (смена сессии, закрытие).
    /// Пустой текст не трогаем: драфт уже отражает пустое состояние (тумблер/отправка
    /// удаляют его сами), а на старте/смене пустое окошко — не «пользователь очистил».
    /// </summary>
    public void Flush()
    {
        var text = _input();
        if (text.Length > 0 && text != _lastSaved)
        {
            _store.Save(_currentSessionId(), text);
            _lastSaved = text;
        }
    }

    /// <summary>Восстановить драфт ТЕКУЩЕЙ сессии в окошко ввода (старт, смена сессии).</summary>
    public void Restore()
    {
        var text = _store.Load(_currentSessionId()) ?? string.Empty;
        _setInput(text);
        _lastSaved = text;
    }

    /// <summary>Отправка: текст стал сообщением — драфт удаляем.</summary>
    public void ClearOnSend()
    {
        _store.Clear(_currentSessionId());
        _lastSaved = string.Empty;
    }
}
