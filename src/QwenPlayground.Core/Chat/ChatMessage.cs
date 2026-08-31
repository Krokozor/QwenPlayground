using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Chat;

public sealed class ChatMessage
{
    /// <summary>
    /// Стабильный ID сообщения: монотонный счётчик сессии (system = 0, остальные 1..N).
    /// Присваивается один раз при входе в разговор, персистится в сессии, НЕ меняется
    /// при компакции/откате (счётчик только растёт). Рендерится как &lt;id=N&gt; в начале
    /// сообщения — служебный «якорь» для мета-данных и инструментов (см. QwenChatTemplate).
    /// </summary>
    public int Id { get; set; }

    public required ChatRole Role { get; init; }
    public string Content { get; set; } = string.Empty;
    public string? Reasoning { get; set; }
    public bool ThinkingClosed { get; set; } = true;
    public IReadOnlyList<ToolCall>? ToolCalls { get; set; }
    public GenerationInfo? Generation { get; set; }

    /// <summary>
    /// State-блок — снапшот системного статуса на момент генерации
    /// (msg_id, time, context cur/max, build, mem, nag). Рендерится в начале think-блока.
    /// У старых сообщений (до введения) — null, тогда блок не рендерится.
    /// </summary>
    public StateBlock? StateBlock { get; set; }

    public string ToRawOutput()
    {
        var think = ThinkContent();
        if (!ThinkingClosed)
        {
            return think;
        }
        if (think.Length == 0)
        {
            return Content;
        }
        return think + "\n" + QwenSpecialTokens.ThinkEnd + "\n\n" + Content;
    }

    /// <summary>
    /// Содержимое think-блока: state-блок (если есть) + reasoning, каждый на своей строке.
    /// Пусто, если и блока, и мыслей нет.
    /// </summary>
    private string ThinkContent()
    {
        if (StateBlock is null)
        {
            return Reasoning ?? string.Empty;
        }
        if (Reasoning is null || Reasoning.Length == 0)
        {
            return StateBlock.ToString();
        }
        return StateBlock.ToString() + "\n" + Reasoning;
    }

    public static ChatMessage System(string content) => new() { Role = ChatRole.System, Content = content };

    public static ChatMessage User(string content) => new() { Role = ChatRole.User, Content = content };

    public static ChatMessage Assistant(string content, string? reasoning = null, IReadOnlyList<ToolCall>? toolCalls = null) =>
        new() { Role = ChatRole.Assistant, Content = content, Reasoning = reasoning, ToolCalls = toolCalls };

    public static ChatMessage Tool(string content) => new() { Role = ChatRole.Tool, Content = content };
}
