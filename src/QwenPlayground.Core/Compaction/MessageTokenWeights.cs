using QwenPlayground.Core.Chat;

namespace QwenPlayground.Core.Compaction;

/// <summary>
/// Точные токены на сообщение из серверных счётчиков, а не chars/4:
/// каждый assistant-ход помнит точный размер промпта на момент генерации
/// (Generation.PromptTokens — prompt_eval_count сервера). Разность двух соседних
/// генераций = точные токены сообщений, добавленных между ними (оверхед — системный
/// промпт, инструменты, state-блок — сокращается в разности). Хвост после последней
/// генерации замыкается точным размером СЛЕДУЮЩЕГО промпта (бюджет-гварда, /tokenize):
/// nextPromptTokens − последний якорь, оверхед сокращается и здесь.
///
/// Сумма спана точная; разбивка внутри спана — пропорционально длине текста
/// (эвристика только для размещения, не для суммы). Отрицательная разность
/// (компакция / снятие полок — промпт УМЕНЬШИЛСЯ) клампится в ноль: сообщения не
/// добавлялись, удалялась голова. Смена модели в середине сессии ломает единицы
/// счёта до/после — редкий случай, бюджет-гварда пересчитает после следующей
/// компакции.
///
/// Без якорей (свежая сессия без генераций) — null: вызывающий падает на chars/4.
/// </summary>
public static class MessageTokenWeights
{
    public static int[]? Compute(IReadOnlyList<ChatMessage> messages, int nextPromptTokens)
    {
        if (messages.Count == 0)
        {
            return null;
        }
        var anchors = new List<(int Index, int Tokens)>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is { Role: ChatRole.Assistant, Generation.PromptTokens: > 0 } assistant)
            {
                anchors.Add((i, assistant.Generation.PromptTokens.Value));
            }
        }
        if (anchors.Count == 0)
        {
            return null;
        }

        var weights = new int[messages.Count];
        // [0..первый якорь): разности нет (первый промпт уже содержал голову) — chars/4.
        for (var i = 0; i < anchors[0].Index; i++)
        {
            weights[i] = ContextCompactor.EstimateChars(messages[i]) / 4;
        }
        // [a_k..a_{k+1}): сумма = разность якорей (кламп — см. класс).
        for (var k = 0; k + 1 < anchors.Count; k++)
        {
            AssignSpan(messages, weights, anchors[k].Index, anchors[k + 1].Index,
                Math.Max(0, anchors[k + 1].Tokens - anchors[k].Tokens));
        }
        // [последний якорь..конец): сумма = точный следующий промпт − последний якорь.
        var last = anchors[^1];
        AssignSpan(messages, weights, last.Index, messages.Count,
            Math.Max(0, nextPromptTokens - last.Tokens));
        return weights;
    }

    /// <summary>Раскладывает точную сумму спана по сообщениям [from..to) пропорционально длине текста.</summary>
    private static void AssignSpan(IReadOnlyList<ChatMessage> messages, int[] weights, int from, int to, int total)
    {
        if (to <= from || total <= 0)
        {
            return;
        }
        var lengths = new int[to - from];
        var sum = 0;
        for (var i = from; i < to; i++)
        {
            lengths[i - from] = TextLength(messages[i]);
            sum += lengths[i - from];
        }
        if (sum == 0)
        {
            return;
        }
        var assigned = 0;
        for (var i = from; i < to; i++)
        {
            var share = (int)((long)total * lengths[i - from] / sum);
            weights[i] = share;
            assigned += share;
        }
        // Остаток от деления — самому большому сообщению: сумма спана остаётся точной.
        if (assigned != total)
        {
            var largest = from;
            for (var i = from + 1; i < to; i++)
            {
                if (lengths[i - from] > lengths[largest - from])
                {
                    largest = i;
                }
            }
            weights[largest] += total - assigned;
        }
    }

    private static int TextLength(ChatMessage message)
    {
        var chars = message.Content.Length + (message.Reasoning?.Length ?? 0);
        if (message.ToolCalls is not null)
        {
            foreach (var call in message.ToolCalls)
            {
                chars += call.Name.Length + call.Arguments.ToJsonString().Length;
            }
        }
        return chars;
    }
}
