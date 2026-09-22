using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Точные веса сообщений из серверных счётчиков (Generation.PromptTokens-якоря +
/// точный размер следующего промпта). Инвариант: сумма весов спана = точная сумма
/// (разность якорей / хвост до nextPrompt), разбивка внутри спана — по длине текста.
/// </summary>
public sealed class MessageTokenWeightsTests
{
    private static ChatMessage AssistantWith(int promptTokens, string content = "a")
    {
        var message = ChatMessage.Assistant(content);
        message.Generation = new GenerationInfo
        {
            Prompt = string.Empty,
            PromptTokens = promptTokens
        };
        return message;
    }

    [Fact]
    public void Compute_NoAnchors_ReturnsNull()
    {
        var messages = new[] { ChatMessage.User("привет"), ChatMessage.Assistant("здравствуйте") };

        Assert.Null(MessageTokenWeights.Compute(messages, 100));
    }

    [Fact]
    public void Compute_Empty_ReturnsNull()
    {
        Assert.Null(MessageTokenWeights.Compute(Array.Empty<ChatMessage>(), 100));
    }

    [Fact]
    public void Compute_AnchorDelta_DistributedByTextLength()
    {
        // Якоря: assistant idx1 (pt=1000), assistant idx3 (pt=1030) → спан [1..3) = 30 токенов:
        // assistant "b" (1 символ) + tool "cc" (2 символа) → 10/20. Хвост [3..5) = nextPrompt 1040
        // − 1030 = 10: сам последний якорь "c" (1 символ) + tool "ccc" (3 символа) → 2/8
        // (остаток от деления — большему). Голова [0): chars/4: (1+8)/4 = 2.
        var messages = new[]
        {
            ChatMessage.User("a"),
            AssistantWith(1000, "b"),
            ChatMessage.Tool("cc"),
            AssistantWith(1030, "c"),
            ChatMessage.Tool("ccc")
        };

        var weights = MessageTokenWeights.Compute(messages, nextPromptTokens: 1040);

        Assert.NotNull(weights);
        Assert.Equal([2, 10, 20, 2, 8], weights);
    }

    [Fact]
    public void Compute_SpanSum_StaysExact_UnderDistribution()
    {
        // 3 сообщения разной длины, сумма спана не делится нацело — остаток уходит
        // самому большему, сумма спана точная.
        var messages = new[]
        {
            ChatMessage.User("a"),
            AssistantWith(1000, "b"),
            ChatMessage.Tool("cc"),
            ChatMessage.Tool("ccc"),
            AssistantWith(1010, "d")
        };

        var weights = MessageTokenWeights.Compute(messages, nextPromptTokens: 1010);

        Assert.NotNull(weights);
        // Спан [1..3): 1000→1010 = 10 токенов на "b"(1), "cc"(2), "ccc"(3) → сумма ровно 10.
        Assert.Equal(10, weights![1] + weights[2] + weights[3]);
        Assert.Equal(0, weights[4]); // хвост: nextPrompt == pt последнего якоря
    }

    [Fact]
    public void Compute_NegativeDelta_ClampedToZero()
    {
        // Промпт УМЕНЬШИЛСЯ (компакция / снятие полок): сообщения спана не добавлялись —
        // кламп в ноль, без отрицательных весов.
        var messages = new[]
        {
            ChatMessage.User("a"),
            AssistantWith(1000, "b"),
            ChatMessage.Tool("cc"),
            AssistantWith(900, "c"),
            ChatMessage.Tool("ccc")
        };

        var weights = MessageTokenWeights.Compute(messages, nextPromptTokens: 910);

        Assert.NotNull(weights);
        Assert.Equal(0, weights![1]);
        Assert.Equal(0, weights[2]);
        Assert.Equal(10, weights[3] + weights[4]); // хвост: 910 − 900
    }

    [Fact]
    public void Compute_MultipleEras_DeltasAcrossCompactionDrop()
    {
        // Два «эра»: рост 1000→1040, провал (компакция) до 200, рост 200→240.
        // Дельты внутри эр учитываются (это сообщения, добавленные в свои эпохи),
        // сам провал (−840) клампится в ноль — голова удалялась, а не добавлялась.
        var messages = new List<ChatMessage> { ChatMessage.User(new string('u', 40)) };
        for (var era = 0; era < 2; era++)
        {
            for (var i = 0; i < 5; i++)
            {
                messages.Add(AssistantWith(0, new string('a', 40)));
                messages.Add(ChatMessage.Tool(new string('t', 40)));
            }
        }
        // Якоря на каждом assistant (индексы 1,3,...,19): эра 0 — 1000..1040, эра 1 — 200..240.
        for (var i = 1; i < messages.Count; i += 2)
        {
            var era = i < 10 ? 0 : 1;
            var step = (i - (era == 0 ? 1 : 11)) / 2; // 0..4 внутри эры
            messages[i].Generation = new GenerationInfo
            {
                Prompt = string.Empty,
                PromptTokens = (era == 0 ? 1000 : 200) + step * 10
            };
        }

        var weights = MessageTokenWeights.Compute(messages, nextPromptTokens: 250);

        Assert.NotNull(weights);
        // Сумма = голова chars/4 (12) + дельты эры 0 (4×10) + провал (0) + дельты эры 1 (4×10) + хвост (250−240).
        Assert.Equal((40 + 8) / 4 + 40 + 0 + 40 + 10, weights!.Sum());
        Assert.All(weights, w => Assert.True(w >= 0));
    }
}
