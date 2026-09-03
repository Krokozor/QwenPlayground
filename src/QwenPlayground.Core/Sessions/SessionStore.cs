using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.Sessions;

public sealed record SessionInfo(string Id, string Title, DateTime UpdatedAt)
{
    // ComboBox в ChatView биндится на этот тип; осмысленный ToString — чтобы
    // при любом провале DisplayMemberPath показывался заголовок, а не "SessionInfo {…}".
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? Id : Title;
}

public sealed class SessionData
{
    public required string Id { get; set; }

    /// <summary>
    /// Версия формата файла. 0 = файл не прошёл через Save (legacy, поле отсутствовало) —
    /// сигнал для миграции при первом ломающем изменении формата. Save всегда штампует
    /// актуальную версию.
    /// </summary>
    public int FormatVersion { get; set; }

    public string Title { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>
    /// Цель сессии (тип взаимодействия с агентом). Сегодня всегда "chat"; поле — фундамент
    /// типизированных целей (research, дочерний агент оркестратора и т.п.): файл сам знает,
    /// для чего он. Отсутствует в legacy-файлах → инициализатор даёт "chat".
    /// </summary>
    public string Purpose { get; set; } = "chat";

    /// <summary>
    /// Ключи кусков профиля чата из config/chat-profiles.json (семплер / промпт /
    /// state-блок назначаются независимо). null — кусок default: поведение как раньше.
    /// </summary>
    public string? SamplerKey { get; set; }
    public string? PromptKey { get; set; }
    public string? StateBlockKey { get; set; }

    /// <summary>
    /// Следующий свободный ID сообщения (монотонный счётчик сессии). Только растёт —
    /// при откате/компакции не уменьшается, чтобы ID не переиспользовались (иначе
    /// dangling-референс мог тихо указывать на другое сообщение). Старые сессии без
    /// поля — 0, при загрузке выводится как max(Id)+1 (миграция).
    /// </summary>
    public int NextMessageId { get; set; }
}

/// <summary>
/// Хранилище сессий: каждая сессия живёт в своей папке sessions/&lt;id&gt;/ с файлом
/// chat.json внутри (аналогично main-сессии: sessions/main/chat.json + слои + артефакты).
/// Список сессий отдаётся из index.json (id/title/updatedAt), чтобы List() не
/// десериализовывал целиком каждый файл разговора ради заголовка.
/// index.json — кэш: файлы, добавленные мимо хранилища, читаются по метаданным,
/// а битый/отсутствующий индекс молча восстанавливается на следующем Save.
/// Старые плоско лежащие файлы sessions/&lt;id&gt;.json читаются как legacy (миграция
/// в папки выполняется на следующем Save).
/// </summary>
public sealed class SessionStore
{
    private const string IndexName = "index";
    private const string ChatFile = "chat";

    /// <summary>Текущая версия формата chat.json; штампуется при каждом Save.</summary>
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly string _indexFile;
    private readonly Dictionary<string, SessionInfo> _index;

    public SessionStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _indexFile = Path.Combine(directory, IndexName + ".json");
        _index = LoadIndex(_indexFile);
    }

    /// <summary>Каталог сессии (sessions/&lt;id&gt;/).</summary>
    private string SessionFolder(string id) => Path.Combine(_directory, id);

    private string ChatFilePath(string id) => Path.Combine(_directory, id, ChatFile + ".json");

    private string LegacyFilePath(string id) => Path.Combine(_directory, id + ".json");

    public IReadOnlyList<SessionInfo> List()
    {
        var byId = new Dictionary<string, SessionInfo>(StringComparer.Ordinal);
        // Новая структура: папка на сессию. Каждая подпапка с chat.json — сессия.
        foreach (var folder in Directory.EnumerateDirectories(_directory))
        {
            var id = Path.GetFileName(folder);
            var chatFile = Path.Combine(folder, ChatFile + ".json");
            if (!File.Exists(chatFile))
            {
                continue;
            }
            // Идентичность сессии — имя папки; вложенный id мог отличаться в legacy-файлах.
            var info = _index.TryGetValue(id, out var cached) ? cached : ReadSessionInfo(chatFile);
            if (info is not null)
            {
                byId[id] = new SessionInfo(id, info.Title, info.UpdatedAt);
            }
        }
        // Legacy: плоские файлы sessions/<id>.json.
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (id.Equals(IndexName, StringComparison.OrdinalIgnoreCase) || byId.ContainsKey(id))
            {
                continue;
            }
            var info = _index.TryGetValue(id, out var cached) ? cached : ReadSessionInfo(file);
            if (info is not null)
            {
                byId[id] = new SessionInfo(id, info.Title, info.UpdatedAt);
            }
        }
        return byId.Values.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public void Save(string id, IReadOnlyList<ChatMessage> messages, string? title = null, int nextMessageId = 0, string purpose = "chat",
        string? samplerKey = null, string? promptKey = null, string? stateBlockKey = null)
    {
        var finalTitle = title;
        if (finalTitle is null)
        {
            finalTitle = messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Content.Trim() ?? "новая сессия";
            if (finalTitle.Length > 48)
            {
                finalTitle = finalTitle[..48] + "…";
            }
        }
        var data = new SessionData
        {
            Id = id,
            Title = finalTitle,
            UpdatedAt = DateTime.Now,
            Messages = messages.ToList(),
            NextMessageId = nextMessageId,
            FormatVersion = CurrentFormatVersion,
            Purpose = purpose,
            SamplerKey = samplerKey,
            PromptKey = promptKey,
            StateBlockKey = stateBlockKey
        };
        Directory.CreateDirectory(SessionFolder(id));
        AtomicFile.WriteAllText(ChatFilePath(id), Serialize(data));
        // Убрать устаревший плоский файл sessions/<id>.json, если он остался от старой структуры.
        var legacy = LegacyFilePath(id);
        if (File.Exists(legacy))
        {
            File.Delete(legacy);
        }
        _index[id] = new SessionInfo(id, finalTitle, data.UpdatedAt);
        PersistIndex();
    }

    /// <summary>Сериализация SessionData в том же формате, что и файлы сессий (нужно бэкапам).</summary>
    public static string Serialize(SessionData data) => JsonSerializer.Serialize(data, Options);

    public SessionData? Load(string id)
    {
        var file = ChatFilePath(id);
        if (File.Exists(file))
        {
            return JsonSerializer.Deserialize<SessionData>(File.ReadAllText(file), Options);
        }
        // Legacy: плоский файл sessions/<id>.json из старой структуры.
        var legacy = LegacyFilePath(id);
        return File.Exists(legacy)
            ? JsonSerializer.Deserialize<SessionData>(File.ReadAllText(legacy), Options)
            : null;
    }

    public void Delete(string id)
    {
        var dir = SessionFolder(id);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
        else
        {
            var legacy = LegacyFilePath(id);
            if (File.Exists(legacy))
            {
                File.Delete(legacy);
            }
        }
        if (_index.Remove(id))
        {
            PersistIndex();
        }
    }

    private void PersistIndex()
    {
        try
        {
            var root = new JsonObject();
            foreach (var (id, info) in _index)
            {
                root[id] = new JsonObject { ["title"] = info.Title, ["updatedAt"] = info.UpdatedAt };
            }
            AtomicFile.WriteAllText(_indexFile, root.ToJsonString());
        }
        catch
        {
            // индекс — кэш; при потере восстановится из файлов сессий
        }
    }

    private static Dictionary<string, SessionInfo> LoadIndex(string indexFile)
    {
        var result = new Dictionary<string, SessionInfo>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(indexFile))
            {
                return result;
            }
            using var document = JsonDocument.Parse(File.ReadAllBytes(indexFile));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Value.TryGetProperty("title", out var title) ||
                    !property.Value.TryGetProperty("updatedAt", out var updated) ||
                    updated.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                if (DateTime.TryParse(updated.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
                {
                    result[property.Name] = new SessionInfo(property.Name, title.GetString() ?? string.Empty, date);
                }
            }
        }
        catch
        {
        }
        return result;
    }

    /// <summary>Потоковое чтение только метаданных файла сессии, без материализации messages.</summary>
    private static SessionInfo? ReadSessionInfo(string file)
    {
        try
        {
            var reader = new Utf8JsonReader(File.ReadAllBytes(file));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }
            string? id = null;
            string? title = null;
            DateTime? updatedAt = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    reader.Skip();
                    continue;
                }
                var name = reader.GetString();
                if (!reader.Read())
                {
                    break;
                }
                switch (name)
                {
                    case "id":
                        id = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                        break;
                    case "title":
                        title = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                        break;
                    case "updatedAt":
                        updatedAt = reader.TokenType == JsonTokenType.String &&
                                    DateTime.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
                            ? date
                            : null;
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            return id is not null && updatedAt is not null
                ? new SessionInfo(id, title ?? string.Empty, updatedAt.Value)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
