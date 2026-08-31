using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Memory;

namespace QwenPlayground.Core.Tests;

public sealed class DialogueWindowTests
{
    [Fact]
    public void Build_KeepsChronologicalOrderWithinBudget()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("привет"),
            ChatMessage.Assistant("привет и тебе"),
            ChatMessage.User("какие задачи?")
        };

        var result = DialogueWindow.Build(messages, budgetTokens: 1000);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Msg 1 (User)", lines[0]);
        Assert.StartsWith("Msg 2 (Model)", lines[1]);
        Assert.StartsWith("Msg 3 (User)", lines[2]);
        Assert.DoesNotContain("Msg 4", result);
    }

    [Fact]
    public void Build_DropsOldestWhenBudgetIsTight()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("старый"),                      // ~1 токен
            ChatMessage.Assistant(new string('a', 400)),     // ~100 токенов
            ChatMessage.User("свежий интент")                // ~3 токена
        };

        // Бюджет 100 токенов: свежий (5) влезает, ассистент (102) — граница, берётся хвост на остаток.
        var result = DialogueWindow.Build(messages, budgetTokens: 100);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length); // свежий целиком + хвост ассистента; «старый» выброшен
        Assert.StartsWith("Msg 2 (Model)", lines[0]);
        Assert.StartsWith("Msg 3 (User)", lines[1]);
        Assert.DoesNotContain("старый", result);
    }

    [Fact]
    public void Build_GiantMessage_TakesTailUpToBudget()
    {
        var head = new string('H', 2000);
        var tail = new string('T', 100) + "TAILMARK";
        // Один гигантский ответ: ~600 токенов при бюджете 40 — ни один другой не разместить.
        var messages = new List<ChatMessage> { ChatMessage.Assistant(head + tail) };

        var result = DialogueWindow.Build(messages, budgetTokens: 40);

        Assert.Contains("TAILMARK", result);
        Assert.DoesNotContain(new string('H', 1000), result);
    }

    [Fact]
    public void Build_EmptyOrFilledWithNoise_YieldsEmpty()
    {
        Assert.Equal(string.Empty, DialogueWindow.Build(Array.Empty<ChatMessage>()));
        Assert.Equal(string.Empty, DialogueWindow.Build(new[] { ChatMessage.Tool("обрез"), ChatMessage.User("   ") }));
    }

    [Fact]
    public void Build_CapsAtMaxMessages()
    {
        var messages = Enumerable.Range(0, 6).Select(i => ChatMessage.User("сообщение " + i)).ToList();

        var result = DialogueWindow.Build(messages, budgetTokens: 10_000, maxMessages: 3);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains("Msg 1 (User): \"сообщение 3\"", result);
        Assert.DoesNotContain("сообщение 0", result);
        Assert.DoesNotContain("сообщение 1", result);
    }

    [Fact]
    public void EstimateTokens_IsCharsOverFour()
    {
        Assert.Equal(3, DialogueWindow.EstimateTokens("abcd")); // (4+8)/4
        Assert.True(DialogueWindow.EstimateTokens(new string('a', 1000)) >= 250);
    }
}