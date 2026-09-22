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
        // Единственное жёсткое ограничение: хвост не может НАЧИНАТЬСЯ с tool-сообщения
        // (tool-результат без своего вызова — шаблон отрендерит битый чат), т.е. цепочка
        // assistant(tool_calls) → tool[ → tool...] не должна резаться. Привязки к
        // user-сообщениям нет намеренно: user-запрос — триггер, а ценный контекст — работа
        // ассистента (тулы, выводы); резать границу под user-а значит отдавать в суммаризацию
        // самое ценное и дословно хранить самое дешёвое. Бюджетное место в середине
        // тул-цепочки — цепочка целиком (с assistant'ом) уходит в хвост: хвост растёт на
        // пару сообщений, безопасное направление.
        while (boundary > firstKept && boundary < messages.Count && messages[boundary].Role == ChatRole.Tool)
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
