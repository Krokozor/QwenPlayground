using System.Text;
using QwenPlayground.Core.Chat;

namespace QwenPlayground.Core.Compaction;

public static class ContextCompactor
{
    /// <summary>Доля недавних сообщений, которую компактация сохраняет дословно (хвост).</summary>
    public const double DefaultKeepRatio = 0.5;

    /// <summary>
    /// Резерв свободного контекста для порога компакции: сжимаем, когда свободного места
    /// не хватает на генерацию (MaxTokens) плюс этот запас на служебные LLM-вызовы
    /// (суммаризация/слои). Единый источник для бюджет-гварда, пред-отправочной проверки
    /// и порога на вкладке «Диагностика».
    /// </summary>
    public const int CompactionReserveTokens = 1000;

    public static int EstimateTokens(IEnumerable<ChatMessage> messages)
    {
        var chars = 0;
        foreach (var message in messages)
        {
            chars += EstimateChars(message);
        }
        return chars / 4;
    }

    /// <summary>
    /// keepRatio — доля недавних сообщений, сохраняемая дословно; остальное уходит в резюме.
    /// windowSize — окно модели: хвост не превышает ~70% окна, иначе следующая генерация не влезет.
    /// </summary>
    public static int FindCompactionBoundary(IReadOnlyList<ChatMessage> messages, double keepRatio, int windowSize = 0)
    {
        var total = EstimateTokens(messages);
        var keepBudget = (int)(total * keepRatio);
        if (windowSize > 0)
        {
            keepBudget = Math.Min(keepBudget, (int)(windowSize * 0.7));
        }

        var boundary = messages.Count;
        var tail = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var estimate = EstimateChars(messages[i]) / 4;
            if (tail >= keepBudget)
            {
                break;
            }
            tail += estimate;
            boundary = i;
        }

        var firstKept = messages.Count > 0 && messages[0].Role == ChatRole.System ? 1 : 0;
        while (boundary < messages.Count && messages[boundary].Role != ChatRole.User)
        {
            boundary++;
        }
        // Граница не может стоять сразу после assistant с tool_calls: tool-результаты
        // остались бы в хвосте без своего вызова — шаблон отрендерит битый чат.
        while (boundary > firstKept && messages[boundary - 1].ToolCalls is { Count: > 0 })
        {
            boundary--;
        }
        return boundary > firstKept && boundary < messages.Count ? boundary : 0;
    }

    // Транскрипт суммаризации не должен переполнять окно модели.
    private const int DefaultCap = 2000;

    /// <summary>Текстовый транскрипт сегмента: ### роль, [thoughts], [call name(args)] — для суммаризации и извлечения памяти.</summary>
    public static string BuildTranscript(IReadOnlyList<ChatMessage> messages, int endExclusive, int cap = DefaultCap)
    {
        var transcript = new StringBuilder();
        for (var i = 0; i < endExclusive && i < messages.Count; i++)
        {
            var message = messages[i];
            transcript.Append("### ").Append(message.Role.ToString().ToLowerInvariant()).Append('\n');
            // State-блок (msg_id, time, context, build, mem) — та же служебная метка, что и в живом
            // промпте: суммаризатору нужна хронология сегмента (время, рост контекста, смена билдов),
            // а не голый текст. У старых сообщений (до введения блока) — пропускается.
            if (message.StateBlock is { } stateBlock)
            {
                transcript.Append(stateBlock).Append('\n');
            }
            if (message.Reasoning is { Length: > 0 } reasoning)
            {
                transcript.Append("[thoughts] ").Append(Cap(reasoning, cap)).Append('\n');
            }
            if (message.Content.Length > 0)
            {
                transcript.Append(Cap(message.Content, cap)).Append('\n');
            }
            if (message.ToolCalls is not null)
            {
                foreach (var call in message.ToolCalls)
                {
                    transcript.Append("[call] ").Append(call.Name).Append('(').Append(call.Arguments.ToJsonString()).Append(')').Append('\n');
                }
            }
            transcript.Append('\n');
        }
        return transcript.ToString();
    }

    private static int EstimateChars(ChatMessage message)
    {
        var chars = message.Content.Length + (message.Reasoning?.Length ?? 0);
        if (message.ToolCalls is not null)
        {
            foreach (var call in message.ToolCalls)
            {
                chars += call.Name.Length + call.Arguments.ToJsonString().Length;
            }
        }
        return chars + 8;
    }

    private static string Cap(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + $"\n... (+{text.Length - limit} chars)";
}
