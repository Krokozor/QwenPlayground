using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Inference;
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

    // Слот llama.cpp текущей сессии (идёт в ход её /completion как id_slot).
    // null — сервер выбирает (LRU). Дефолты схемы: main → 0, не-main → 1 (SlotAllocation).
    private int? _slotId;

    // Рабочая папка текущей сессии (root инструментов, план 2026-10-01). null — не
    // задано: фоллбек в глобальную AppSettings.ProjectRoot. Живёт в SessionData.
    private string? _root;

    public SessionController(ChatLog log, DraftKeeper draft, MemorySurfacer surfacer, string? sessionsRoot = null)
    {
        _log = log;
        _draft = draft;
        _surfacer = surfacer;
        // Шов для тестов: изолированный каталог (как у ChatSessions).
        _sessions = new ChatSessions(sessionsRoot);
        // Счётчик id — на каждое сообщение в сайдкар (sessions/<id>/counter): полный
        // chat.json сохраняется реже, rebuild может убить процесс между сохранением и
        // последними сообщениями → без сайдкара новая загрузка возвращала счётчик назад
        // (id переиспользовались, старые артефакты рендерились в новых сообщениях).
        _log.Added += _ => _sessions.TouchCounter(_sessions.CurrentId, _log.NextMessageId);
    }

    public string CurrentId => _sessions.CurrentId;
    public string DirectoryFor(string id) => _sessions.DirectoryFor(id);

    /// <summary>Счётчик стабильных id сообщений (сайдкар sessions/&lt;id&gt;/counter) — на каждое добавление.</summary>
    public void TouchCounter(string id, int nextMessageId) => _sessions.TouchCounter(id, nextMessageId);
    public string? SamplerKey => _samplerKey;
    public string? PromptKey => _promptKey;
    public string? StateBlockKey => _stateBlockKey;

    /// <summary>Слот llama.cpp текущей сессии (null — LRU сервера).</summary>
    public int? CurrentSlotId => _slotId;

    /// <summary>Рабочая папка текущей сессии (null — не задано, действует глобальный дефолт).</summary>
    public string? Root => _root;

    /// <summary>
    /// Рабочая папка сессии сменилась (UI или тул set_session_root): вид обновляет
    /// поле root и превью (системный промпт несёт resolved root).
    /// </summary>
    public event Action? RootChanged;

    /// <summary>
    /// Назначить слот текущей сессии (выбор в UI) + сохранить сразу — редкое событие,
    /// выбор не должен теряться при закрытии. Ходы сессии пойдут в этот слот (id_slot).
    /// </summary>
    public void SetCurrentSlot(int? slotId)
    {
        _slotId = slotId;
        SaveCurrent();
    }

    /// <summary>
    /// Назначить рабочую папку текущей сессии (UI или тул set_session_root) + сохранить
    /// сразу. Валидация: абсолютный путь, папка существует; тул/метод НЕ создают папку.
    /// Возвращает текст ошибки; null — успех (сессия сохранена).
    /// </summary>
    public string? SetRoot(string path)
    {
        var error = ValidateRoot(path, out var full);
        if (error is not null)
        {
            return error;
        }
        _root = full;
        SaveCurrent();
        RootChanged?.Invoke();
        return null;
    }

    /// <summary>Сбросить рабочую папку текущей сессии (вернуться к глобальному дефолту).</summary>
    public void ClearRoot()
    {
        if (_root is null)
        {
            return;
        }
        _root = null;
        SaveCurrent();
        RootChanged?.Invoke();
    }

    /// <summary>
    /// Валидация рабочей папки: непустой абсолютный путь, каталог существует.
    /// Возвращает текст ошибки; null — ок (fullPath — нормализованный путь).
    /// </summary>
    public static string? ValidateRoot(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return "путь пуст — укажите рабочую папку";
        }
        if (!Path.IsPathRooted(path))
        {
            return $"путь не абсолютный: {path}";
        }
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception)
        {
            return $"некорректный путь: {exception.Message}";
        }
        if (!Directory.Exists(fullPath))
        {
            return $"папка не существует: {fullPath}";
        }
        return null;
    }

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
            _slotId = data.SlotId ?? SlotAllocation.Main; // main по умолчанию на слоте 0 (легаси)
            _root = data.Root;
        }
        else
        {
            StartupTrace.Log($"EnsureMain: created empty ({sw.ElapsedMilliseconds}ms)");
            _log.Clear();
            _slotId = SlotAllocation.Main;
            _root = null;
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
        // Слот сессии: из файла; без поля — дефолт схемы (main → 0, не-main → 1).
        _slotId = data.SlotId ?? (id == MainAgent.SessionId ? SlotAllocation.Main : SlotAllocation.NonMain);
        _root = data.Root;
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
        _slotId = SlotAllocation.NonMain; // новая не-main сессия — дефолт схемы (1)
        _root = null;
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
            _slotId = SlotAllocation.NonMain; // свежая пустая сессия — не-main, дефолт схемы
            _root = null;
        }
        SessionChanged?.Invoke();
        return deletedCurrent;
    }

    /// <summary>
    /// Уборка на старте: снести ПУСТЫЕ каталоги сессий (см. ChatSessions.PruneEmptyFolders).
    /// main и текущая сессия не трогаются. Возвращает id удалённых каталогов.
    /// </summary>
    public IReadOnlyList<string> PruneEmptyFolders() => _sessions.PruneEmptyFolders();

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

    /// <summary>Персистентность текущей истории + ключей профилей + слота + рабочей папки.</summary>
    public void SaveCurrent()
    {
        _log.AssignPendingIds();
        _sessions.SaveCurrent(_log, _log.NextMessageId,
            samplerKey: _samplerKey, promptKey: _promptKey, stateBlockKey: _stateBlockKey, slotId: _slotId, root: _root);
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
    /// Загрузить данные сессии БЕЗ побочных эффектов (не трогает текущую: CurrentId,
    /// драфт, ключи, surfaced-память): для reopening закреплённого окна (субагент) на
    /// той же сессии. Чистое чтение через TryLoadData (Load переключил бы main-окно!).
    /// </summary>
    public SessionData? LoadData(string id) => _sessions.TryLoadData(id);

    /// <summary>
    /// Сохранить историю закреплённой сессии (рантайм субагента), не трогая текущую:
    /// лог — из рантайма, ключи профилей и рабочая папка — из рантайма.
    /// </summary>
    public void SavePinned(string id, ChatLog log, string? samplerKey = null, string? promptKey = null, string? stateBlockKey = null,
        string? root = null)
    {
        log.AssignPendingIds();
        _sessions.Save(id, log, log.NextMessageId, samplerKey: samplerKey, promptKey: promptKey, stateBlockKey: stateBlockKey, root: root);
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
