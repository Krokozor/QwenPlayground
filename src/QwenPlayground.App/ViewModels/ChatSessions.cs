using System.Collections.ObjectModel;
using System.IO;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Жизненный цикл сессий чата (домен, вытащенный из MainViewModel): текущий ID, список
/// для UI-панели, миграция legacy-main с бэкапом, персистенция «последней открытой».
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
    private readonly MemoryLayerStore _layerStore;

    /// <summary>Идентификатор текущей сессии; main-агент — до первого переключения.</summary>
    public string CurrentId { get; private set; } = MainAgent.SessionId;

    /// <summary>Список сессий для UI (перестраивается RefreshList).</summary>
    public ObservableCollection<SessionInfo> List { get; } = new();

    public ChatSessions(string? root = null, MemoryLayerStore? layerStore = null)
    {
        // Швы для тестов: изолированный каталог и собственный слой памяти.
        _root = root ?? Root;
        _store = new SessionStore(_root);
        _layerStore = layerStore ?? new MemoryLayerStore();
    }

    public string DirectoryFor(string id) => Path.Combine(_root, id);

    /// <summary>
    /// Гарантирует существование main-сессии (мигрирует legacy-формат при необходимости)
    /// и возвращает её данные. null — main пуст: начинаем с чистого разговора.
    /// </summary>
    public SessionData? EnsureMain()
    {
        EnsureMainMigrated();
        return _store.Load(MainAgent.SessionId);
    }

    /// <summary>
    /// Однократная миграция sessions/main.json → sessions/main/chat.json. Старое резюме
    /// из system-сообщения не выбрасываем: оно становится семенем слоя L1. Исходный файл
    /// уносится в backups/ на случай отката.
    /// </summary>
    private void EnsureMainMigrated()
    {
        if (_store.Load(MainAgent.SessionId) is not null)
        {
            return;
        }
        var legacy = Path.Combine(_root, MainAgent.SessionId + ".json");
        if (!File.Exists(legacy))
        {
            return;
        }
        var legacyStore = new SessionStore(_root);
        var data = legacyStore.Load(MainAgent.SessionId);
        if (data is null)
        {
            return;
        }

        var messages = data.Messages.ToList();
        string? legacySummary = null;
        if (messages.Count > 0 && messages[0].Role == ChatRole.System)
        {
            var markerIndex = messages[0].Content.IndexOf(ContextCompactor.SummaryMarker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                legacySummary = messages[0].Content[(markerIndex + ContextCompactor.SummaryMarker.Length)..].Trim();
            }
            messages.RemoveAt(0);
        }
        if (!string.IsNullOrEmpty(legacySummary))
        {
            var layers = _layerStore.Load();
            if (layers.IsEmpty)
            {
                layers.L1 = legacySummary;
                _layerStore.Save(layers);
            }
        }

        _store.Save(MainAgent.SessionId, messages, MainTitle);
        var backupDir = Path.Combine(SelfBuildPaths.WorkspaceRoot, "backups");
        Directory.CreateDirectory(backupDir);
        var target = Path.Combine(backupDir, $"legacy-main-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.Move(legacy, target);
    }

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
