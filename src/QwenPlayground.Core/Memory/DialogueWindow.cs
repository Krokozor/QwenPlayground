using System.Text;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Memory;

/// <summary>
/// Вектор диалога для классификации: суффикс последних сообщений в пределах бюджета токенов.
/// Идём с конца: сообщения входят целиком; самое старое из вошедших, что не влезло, берётся
/// хвостом. Так один гигантский think/ответ не съедает весь бюджет и не глушит интент владельца.
/// </summary>
public static class DialogueWindow
{
    public static int DefaultBudgetTokens => AppSettings.Get().MemoryDialogueBudgetTokens;
    public static int DefaultMaxMessages => AppSettings.Get().MemoryDialogueMaxMessages;

    public static string Build(
        IEnumerable<ChatMessage> messages,
        int budgetTokens = 0,
        int maxMessages = 0)
    {
        if (budgetTokens <= 0) budgetTokens = DefaultBudgetTokens;
        if (maxMessages <= 0) maxMessages = DefaultMaxMessages;

        var relevant = messages
            .Where(m => m.Role is ChatRole.User or ChatRole.Assistant && m.Content.Trim().Length > 0)
            .TakeLast(maxMessages)
            .ToList();
        if (relevant.Count == 0 || budgetTokens <= 0)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        var remaining = budgetTokens;
        for (var i = relevant.Count - 1; i >= 0; i--)
        {
            var message = relevant[i];
            var text = ComposeText(message);
            var estimate = EstimateTokens(text);
            if (estimate <= remaining)
            {
                lines.Add(FormatLine(message, i + 1, text));
                remaining -= estimate;
                continue;
            }
            if (remaining > 0)
            {
                lines.Add(FormatLine(message, i + 1, text, maxChars: remaining * 4));
            }
            break;
        }
        lines.Reverse();
        return string.Join('\n', lines).Trim();
    }

    public static int EstimateTokens(string text) => (text.Length + 8) / 4;

    private static string ComposeText(ChatMessage message)
    {
        var content = message.Content ?? string.Empty;
        if (message.Reasoning is { Length: > 0 } reasoning)
        {
            return reasoning + "\n" + content;
        }
        return content;
    }

    private static string FormatLine(ChatMessage message, int index, string text, int? maxChars = null)
    {
        var role = message.Role == ChatRole.User ? "User" : "Model";
        var oneLine = OneLine(text);
        if (maxChars is { } cap && oneLine.Length > cap)
        {
            oneLine = oneLine[^cap..];
        }
        return $"Msg {index} ({role}): \"{oneLine}\"";
    }

    /// <summary>Хвост сохраняет актуальное направление диалога независимо от того, где обрезали.</summary>
    private static string OneLine(string text) =>
        (text ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace("\"", "'");
}