using System.Collections.ObjectModel;
using System.IO;
using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Serialization;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Sessions;

/// <summary>
/// Жизненный цикл сессий чата (домен, вытащенный из MainViewModel): текущий ID, список
/// для UI-панели, персистенция «последней открытой».
///
/// Контентом разговора не владеет — владелец (ViewModel) отдаёт/получает сообщения
/// в параметрах операций и сам перестраивает вид чата при загрузке. Хранилище одно
/// (sessions/), настройки читаются из синглтона.
/// </summary>
public sealed class ChatSessions
{
    private const string MainTitle = "★ main-агент";

    /// <summary>Каталог всех сессий по умолчанию (ContextBackupStore пишет рядом).</summary>
    public static string Root { get; } = Path.Combine(SelfBuildPaths.WorkspaceRoot, "sessions");

    private readonly string _root;
    private readonly SessionStore _store;

    /// <summary>Идентификатор текущей сессии; main-агент — до первого переключения.</summary>
    public string CurrentId { get; private set; } = MainAgent.SessionId;

    /// <summary>Список сессий для UI (перестраивается RefreshList).</summary>
    public ObservableCollection<SessionInfo> List { get; } = new();

    public ChatSessions(string? root = null)
    {
        // Шов для тестов: изолированный каталог.
        _root = root ?? Root;
        _store = new SessionStore(_root);
    }

    public string DirectoryFor(string id) => Path.Combine(_root, id);

    /// <summary>
    /// Возвращает данные main-сессии. null — main пуст: начинаем с чистого разговора.
    /// LoadHealed: счётчик id из сайдкара (а не только поле/файл) — как у Load.
    /// </summary>
    public SessionData? EnsureMain() => LoadHealed(MainAgent.SessionId);

    /// <summary>Загрузить сессию по id и сделать текущей. null — такой сессии нет.</summary>
    public SessionData? Load(string id)
    {
        var data = LoadHealed(id);
        if (data is null)
        {
            return null;
        }
        CurrentId = id;
        PersistCurrentId();
        return data;
    }

    /// <summary>
    /// ЧИТАЮЩАЯ загрузка сессии БЕЗ побочных эффектов (не трогает CurrentId/LastSessionId):
    /// для reopening закреплённых окон (субагент). ВАЖНО: не путать с Load — тот
    /// переключает текущую сессию главного окна (баг 2026-09-23: reopening субагента
    /// молча переключал main-окно на сессию субагента).
    /// </summary>
    public SessionData? TryLoadData(string id) => LoadHealed(id);

    /// <summary>
    /// Сайдкар-счётчик стабильных id сообщений: sessions/&lt;id&gt;/counter (обычный int).
    /// Обновляется на КАЖДОЕ добавление сообщения (полный chat.json сохраняется реже —
    /// rebuild может убить процесс между сохранением и последними сообщениями). При
    /// загрузке NextMessageId = max(поле, счётчик, max ID + 1) — id никогда не
    /// переиспользуются (инцидент 2026-09-23: старые артефакты переиспользованных id
    /// рендерились в новых сообщениях — чужие скриншоты в tool-ответах).
    /// </summary>
    public void TouchCounter(string id, int nextMessageId)
    {
        try
        {
            var path = CounterPath(id);
            var current = 0;
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path), out var parsed))
            {
                current = parsed;
            }
            if (nextMessageId > current)
            {
                AtomicFile.WriteAllText(path, nextMessageId.ToString());
            }
        }
        catch
        {
            // Счётчик best-effort: без него остаётся самовосстановление из max ID в chat.json.
        }
    }

    private string CounterPath(string id) => Path.Combine(_root, id, "counter");

    private int ReadCounter(string id)
    {
        try
        {
            var path = CounterPath(id);
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path), out var value))
            {
                return value;
            }
        }
        catch
        {
        }
        return 0;
    }

    /// <summary>Load + подлечивание счётчика из сайдкар-файла (поле могло устареть).</summary>
    private SessionData? LoadHealed(string id)
    {
        var data = _store.Load(id);
        if (data is not null)
        {
            var counter = ReadCounter(id);
            if (counter > data.NextMessageId)
            {
                data.NextMessageId = counter;
            }
        }
        return data;
    }

    /// <summary>Начать новую пустую сессию и сделать её текущей.</summary>
    public void StartNew()
    {
        CurrentId = Guid.NewGuid().ToString("N");
        PersistCurrentId();
    }

    /// <summary>
    /// Удалить сессию из хранилища. true — удалена ТЕКУЩАЯ: CurrentId уже переехал на
    /// свежую пустую сессию, владелец должен очистить чат.
    /// </summary>
    public bool Delete(string id)
    {
        _store.Delete(id);
        // KV-якорь субагентов лежит РЯДОМ с папкой сессии (плоское имя: сервер не
        // принимает разделители в filename) — удаляем вместе с сессией.
        TryDeleteKvAnchor(id);
        if (id != CurrentId)
        {
            return false;
        }
        StartNew();
        return true;
    }

    private void TryDeleteKvAnchor(string id)
    {
        try
        {
            var anchor = Path.Combine(_root, $"{id}-kv.bin");
            if (File.Exists(anchor))
            {
                File.Delete(anchor);
            }
        }
        catch
        {
            // Якорь не критичен — не роняем удаление сессии из-за файловой ошибки.
        }
    }

    /// <summary>Сохранить контент текущей сессии (заголовок main проставляется здесь).</summary>
    public void SaveCurrent(IReadOnlyList<ChatMessage> messages, int nextMessageId, string purpose = "chat",
        string? samplerKey = null, string? promptKey = null, string? stateBlockKey = null, int? slotId = null)
    {
        var title = CurrentId == MainAgent.SessionId ? MainTitle : null;
        _store.Save(CurrentId, messages, title, nextMessageId, purpose, samplerKey, promptKey, stateBlockKey, slotId);
    }

    /// <summary>
    /// Сохранить сессию по id, не трогая CurrentId (закреплённые рантаймы, стадия C).
    /// Заголовок не проставляется (у закреплённых сессий нет main-заголовка).
    /// </summary>
    public void Save(string id, IReadOnlyList<ChatMessage> messages, int nextMessageId, string purpose = "subagent",
        string? samplerKey = null, string? promptKey = null, string? stateBlockKey = null, int? slotId = null) =>
        _store.Save(id, messages, null, nextMessageId, purpose, samplerKey, promptKey, stateBlockKey, slotId);

    /// <summary>Перестроить список из хранилища; main присутствует всегда, даже если ещё не сохранялся.</summary>
    public void RefreshList()
    {
        List.Clear();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in _store.List())
        {
            List.Add(info);
            ids.Add(info.Id);
        }
        if (!ids.Contains(MainAgent.SessionId))
        {
            var mainUpdated = _store.Load(MainAgent.SessionId)?.UpdatedAt ?? DateTime.MinValue;
            List.Add(new SessionInfo(MainAgent.SessionId, MainTitle, mainUpdated));
        }
    }

    /// <summary>Последняя открытая сессия (из settings.json); null/пусто — стартуем на main.</summary>
    public string? LastOpenedId => AppSettings.Get().LastSessionId;

    /// <summary>Запомнить текущую сессию. Смена сессии редка — пишем сразу без дебаунса.</summary>
    public void PersistCurrentId()
    {
        AppSettings.Get().LastSessionId = CurrentId;
        AppSettings.Save();
    }
}
