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

    /// <summary>
    /// keepRatio — доля недавних сообщений, сохраняемая дословно; остальное уходит в резюме.
    /// windowSize — окно модели: хвост не превышает ~70% окна, иначе следующая генерация не влезет.
    /// messageTokens — точные токены сообщений (серверные счётчики, см. <see cref="MessageTokenWeights"/>);
    /// null — оценка chars/4 (фолбэк: нет якорей Generation.PromptTokens / сервер недоступен).
    /// </summary>
    public static int FindCompactionBoundary(
        IReadOnlyList<ChatMessage> messages,
        double keepRatio,
        int windowSize = 0,
        int[]? messageTokens = null)
    {
        int Estimate(int index) =>
            messageTokens is not null && index < messageTokens.Length
                ? messageTokens[index]
                : EstimateChars(messages[index]) / 4;

        var total = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            total += Estimate(i);
        }
        var keepBudget = (int)(total * keepRatio);
        if (windowSize > 0)
        {
            keepBudget = Math.Min(keepBudget, (int)(windowSize * 0.7));
        }

        var boundary = messages.Count;
        var tail = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (tail >= keepBudget)
            {
                break;
            }
            tail += Estimate(i);
            boundary = i;
        }

        var firstKept = messages.Count > 0 && messages[0].Role == ChatRole.System ? 1 : 0;
        // Граница — НА user-сообщении (чистый поворот, хвост начинается с user). Если в
        // хвосте user-а НЕТ (длинный тул-цикл: агент крутит инструменты, последний
        // user-запрос давно в голове) — не «нечего сжимать», а откат к последнему user
        // ДО начала хвоста: запрос пользователя остаётся в хвосте дословно, хвост
        // выходит больше бюджета (безопасное направление — сжимаем меньше).
        var nextUser = -1;
        for (var i = boundary; i < messages.Count; i++)
        {
            if (messages[i].Role == ChatRole.User)
            {
                nextUser = i;
                break;
            }
        }
        if (nextUser >= 0)
        {
            boundary = nextUser;
        }
        else
        {
            var lastUser = -1;
            for (var i = Math.Min(boundary, messages.Count) - 1; i >= firstKept; i--)
            {
                if (messages[i].Role == ChatRole.User)
                {
                    lastUser = i;
                    break;
                }
            }
            boundary = lastUser >= 0 ? lastUser : firstKept;
        }
        // Граница не может стоять сразу после assistant с tool_calls: tool-результаты
        // остались бы в хвосте без своего вызова — шаблон отрендерит битый чат.
        while (boundary > firstKept && messages[boundary - 1].ToolCalls is { Count: > 0 })
        {
            boundary--;
        }
        return boundary > firstKept && boundary < messages.Count ? boundary : 0;
    }

    /// <summary>Символов в сообщении (content + reasoning + tool-вызовы + шаблонные маркеры ~8).</summary>
    public static int EstimateChars(ChatMessage message)
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

    private static string Cap(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + $"\n... (+{text.Length - limit} chars)";
}
