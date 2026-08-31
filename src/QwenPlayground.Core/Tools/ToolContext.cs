using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Runtime;

namespace QwenPlayground.Core.Tools;

public sealed class ToolContext
{
    public string ProjectRoot { get; }

    /// <summary>
    /// Живой список сообщений чата, в котором выполняется инструмент (стабильная ссылка
    /// на разговор агента/главного чата). Нужен для связи «чат ⇔ сообщение ⇔ инструмент»:
    /// инструмент знает свой чат и может обращаться к сообщениям напрямую, не только по ID.
    /// null — у контекстов без чата (автономная работа инструмента).
    /// </summary>
    public IReadOnlyList<ChatMessage>? Conversation { get; }

    /// <summary>
    /// Каталог текущей сессии (sessions/main/ для main-агента, sessions/&lt;id&gt;/ для остальных).
    /// Артефакты сообщений (мультимодальность) живут в &lt;sessionDir&gt;/artifacts/msg_&lt;id&gt;/
    /// — инструменты (attach_image) должны писать в сессию, где реально идёт диалог.
    /// null — у контекстов без сессии (оркестратор): инструмент ведёт себя best-effort.
    /// </summary>
    public string? SessionDir { get; }

    /// <summary>
    /// Доступ к сообщению разговора по стабильному ID (для инструментов, работающих
    /// с мета-данными сообщений: message_edit_content и т.п.). null — если недоступен.
    /// System-сообщение (Id 0) не отдаётся.
    /// </summary>
    public Func<int, ChatMessage?>? GetMessageById { get; }

    /// <summary>Заменяет контент сообщения по ID. true — успех, false — не найдено.</summary>
    public Func<int, string, bool>? SetMessageContent { get; }

    /// <summary>
    /// Коллбэк «факт сохранён» (для memory_add): владелец поднимает запись в surfaced-пул,
    /// чтобы агент увидел её в следующем state-блоке — петля «написал → видит» замыкается.
    /// null — в этом контексте памяти нет (тесты/Harness).
    /// </summary>
    public Action<MemoryItem>? OnFactSaved { get; }

    /// <summary>
    /// Скоуп агента, в котором выполняется инструмент: профиль настроек и маршрут
    /// интерактива читаются через <see cref="Scope"/>, а не из процессной статики —
    /// изолированный агент получает свои значения. null (старые точки сборки) —
    /// fallback на main-скоуп, поведение прежнее.
    /// </summary>
    public AgentRuntime? Runtime { get; }

    /// <summary>Скоуп исполнения: переданный Runtime или main-агент по умолчанию.</summary>
    public AgentRuntime Scope => Runtime ?? AgentRuntime.Main;

    public ToolContext(
        string projectRoot,
        Func<int, ChatMessage?>? getMessageById = null,
        Func<int, string, bool>? setMessageContent = null,
        string? sessionDir = null,
        IReadOnlyList<ChatMessage>? conversation = null,
        Action<MemoryItem>? onFactSaved = null,
        AgentRuntime? runtime = null)
    {
        ProjectRoot = Path.GetFullPath(projectRoot);
        SessionDir = sessionDir;
        GetMessageById = getMessageById;
        SetMessageContent = setMessageContent;
        Conversation = conversation;
        OnFactSaved = onFactSaved;
        Runtime = runtime;
    }

    public string ResolvePath(string path)
    {
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectRoot, path));
        var rootWithSeparator = ProjectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, ProjectRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"path escapes project root: {path}");
        }
        return fullPath;
    }

    public string ToRelative(string fullPath) =>
        Path.GetRelativePath(ProjectRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}
