using System.Collections.ObjectModel;
using System.IO;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.App.ViewModels;

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
    /// </summary>
    public SessionData? EnsureMain() => _store.Load(MainAgent.SessionId);

    /// <summary>Загрузить сессию по id и сделать текущей. null — такой сессии нет.</summary>
    public SessionData? Load(string id)
    {
        var data = _store.Load(id);
        if (data is null)
        {
            return null;
        }
        CurrentId = id;
        PersistCurrentId();
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
        if (id != CurrentId)
        {
            return false;
        }
        StartNew();
        return true;
    }

    /// <summary>Сохранить контент текущей сессии (заголовок main проставляется здесь).</summary>
    public void SaveCurrent(IReadOnlyList<ChatMessage> messages, int nextMessageId, string purpose = "chat",
        string? samplerKey = null, string? promptKey = null, string? stateBlockKey = null)
    {
        var title = CurrentId == MainAgent.SessionId ? MainTitle : null;
        _store.Save(CurrentId, messages, title, nextMessageId, purpose, samplerKey, promptKey, stateBlockKey);
    }

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
