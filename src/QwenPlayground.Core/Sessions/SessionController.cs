using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.MetaInfo;

namespace QwenPlayground.Core.Sessions;

/// <summary>
/// Жизненный цикл сессий (домен, вынесен из MainViewModel): текущая сессия,
/// переключение/создание/удаление, персистенция истории + ключей профилей чата
/// и инварианты вокруг переключения:
///   flush драфта СТАРОЙ сессии → загрузка → ключи профилей →
///   очистка транзитного состояния памяти (surfaced-пул + доска анонсов) →
///   restore драфта НОВОЙ сессии.
///
/// Видом не владеет: ChatLog/драфт/surfacer приходят снаружи, контроллер сам
/// заменяет/очищает лог. Реакции UI (список, выбор, превью, меню полок) — через
/// событие SessionChanged; вид подписывается один раз.
/// </summary>
public sealed class SessionController
{
    private readonly ChatLog _log;
    private readonly DraftKeeper _draft;
    private readonly MemorySurfacer _surfacer;
    private readonly ChatSessions _sessions;

    // Ключи кусков профиля этой сессии; null = кусок default. Живут в SessionData.
    private string? _samplerKey;
    private string? _promptKey;
    private string? _stateBlockKey;

    public SessionController(ChatLog log, DraftKeeper draft, MemorySurfacer surfacer, string? sessionsRoot = null)
    {
        _log = log;
        _draft = draft;
        _surfacer = surfacer;
        // Шов для тестов: изолированный каталог (как у ChatSessions).
        _sessions = new ChatSessions(sessionsRoot);
    }

    public string CurrentId => _sessions.CurrentId;
    public string DirectoryFor(string id) => _sessions.DirectoryFor(id);
    public string? SamplerKey => _samplerKey;
    public string? PromptKey => _promptKey;
    public string? StateBlockKey => _stateBlockKey;

    /// <summary>Список сессий для UI (перестраивается RefreshList).</summary>
    public IReadOnlyList<SessionInfo> List => _sessions.List;

    /// <summary>Сессия сменилась (загружена/создана/удалена): вид обновляет список, выбор, превью.</summary>
    public event Action? SessionChanged;

    /// <summary>
    /// Сессия main-агента — сессия по умолчанию: грузим её из sessions/main/chat.json,
    /// иначе начинаем с чистого разговора. Идентичность (main-agent.md) и слои памяти в
    /// историю не пишутся — они собираются в системный промпт при каждом рендере.
    /// </summary>
    public void EnsureMain()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var data = _sessions.EnsureMain();
        if (data is not null)
        {
            StartupTrace.Log($"EnsureMain: loaded {data.Messages.Count} messages ({sw.ElapsedMilliseconds}ms)");
            _log.ReplaceAll(StripBakedSystem(data.Messages));
            _log.SetNextMessageId(data.NextMessageId);
        }
        else
        {
            StartupTrace.Log($"EnsureMain: created empty ({sw.ElapsedMilliseconds}ms)");
            _log.Clear();
        }
        SaveCurrent();
        StartupTrace.Log($"EnsureMain: saved ({sw.ElapsedMilliseconds}ms total)");
    }

    /// <summary>
    /// Переключиться на сессию: flush драфта старой → загрузить → ключи профилей →
    /// очистить транзитное состояние → restore драфта новой. false — такой сессии нет.
    /// </summary>
    public bool Load(string id)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // ПЕРЕД сменой: выгрести текст текущей (старой) сессии в ЕЁ драфт — иначе
        // набранный за последние секунды черновик потеряется при переключении.
        _draft.Flush();
        var data = _sessions.Load(id);
        if (data is null)
        {
            StartupTrace.Log($"Load({id}): not found ({sw.ElapsedMilliseconds}ms)");
            return false;
        }
        StartupTrace.Log($"Load({id}): parsed {data.Messages.Count} messages ({sw.ElapsedMilliseconds}ms)");

        _log.ReplaceAll(id == MainAgent.SessionId ? StripBakedSystem(data.Messages) : data.Messages);
        _log.SetNextMessageId(data.NextMessageId);
        _samplerKey = data.SamplerKey;
        _promptKey = data.PromptKey;
        _stateBlockKey = data.StateBlockKey;
        // Смена сессии: surfaced-пул памяти и мусорка анонсов — транзитное состояние
        // прошлой сессии, не тащим его в новую (иначе чужие заметки просочатся в state-блок).
        _surfacer.Clear();
        AnnouncementBoard.Clear();
        // ПОСЛЕ смены: восстановить драфт НОВОЙ сессии в окошко (у каждой свой черновик).
        _draft.Restore();
        SessionChanged?.Invoke();
        return true;
    }

    /// <summary>Новая пустая сессия (текущая должна быть сохранена вызывающим).</summary>
    public void StartNew()
    {
        _sessions.StartNew();
        _samplerKey = null;
        _promptKey = null;
        _stateBlockKey = null;
        SessionChanged?.Invoke();
    }

    /// <summary>
    /// Удалить сессию из хранилища. true — удалена ТЕКУЩАЯ: CurrentId уже переехал на
    /// свежую пустую сессию, лог очищен, ключи профилей сброшены (новая сессия не
    /// наследует профиль удалённой).
    /// </summary>
    public bool Delete(string id)
    {
        var deletedCurrent = _sessions.Delete(id);
        if (deletedCurrent)
        {
            _log.Clear();
            _samplerKey = null;
            _promptKey = null;
            _stateBlockKey = null;
        }
        SessionChanged?.Invoke();
        return deletedCurrent;
    }

    /// <summary>
    /// Восстановить последнюю открытую сессию (из settings.json). Если её нет, она равна
    /// main или была удалена — остаёмся на main-агенте (дефолт).
    /// </summary>
    public void RestoreLast()
    {
        var lastId = _sessions.LastOpenedId;
        if (string.IsNullOrEmpty(lastId) || lastId == _sessions.CurrentId)
        {
            return;
        }
        if (!Load(lastId))
        {
            _sessions.PersistCurrentId(); // последняя сессия пропала — фиксируем main, чтобы не пытаться снова
        }
    }

    /// <summary>Персистентность текущей истории + ключей профилей.</summary>
    public void SaveCurrent()
    {
        _log.AssignPendingIds();
        _sessions.SaveCurrent(_log, _log.NextMessageId,
            samplerKey: _samplerKey, promptKey: _promptKey, stateBlockKey: _stateBlockKey);
    }

    /// <summary>Назначить куски профиля текущей сессии (шестерёнка в UI) + сохранить сразу.</summary>
    public void ApplyProfileKeys(string? samplerKey, string? promptKey, string? stateBlockKey)
    {
        _samplerKey = samplerKey;
        _promptKey = promptKey;
        _stateBlockKey = stateBlockKey;
        SaveCurrent(); // редкое событие — пишем сразу, выбор не теряется при закрытии
    }

    /// <summary>
    /// Новая пустая сессия, не привязанная к текущей (окно субагента, стадия C):
    /// id возвращается без переключения главного окна и без персистентности LastSessionId.
    /// Файл создаётся при первом сохранении.
    /// </summary>
    public string CreateDetached() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Сохранить историю закреплённой сессии (рантайм субагента), не трогая текущую:
    /// лог — из рантайма, ключи профилей — из рантайма.
    /// </summary>
    public void SavePinned(string id, ChatLog log, string? samplerKey = null, string? promptKey = null, string? stateBlockKey = null)
    {
        log.AssignPendingIds();
        _sessions.Save(id, log, log.NextMessageId, samplerKey: samplerKey, promptKey: promptKey, stateBlockKey: stateBlockKey);
    }

    /// <summary>Перестроить список сессий из хранилища (для UI).</summary>
    public void RefreshList() => _sessions.RefreshList();

    /// <summary>
    /// У старой main-сессии system-сообщение — запечённая идентичность (+старое резюме).
    /// Теперь идентичность собирается динамически, поэтому снимаем её из истории.
    /// </summary>
    private static List<ChatMessage> StripBakedSystem(IReadOnlyList<ChatMessage> messages) =>
        messages.Count > 0 && messages[0].Role == ChatRole.System ? messages.Skip(1).ToList() : messages.ToList();
}
