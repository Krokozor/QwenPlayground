using QwenPlayground.Core.Probes;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Чистые тесты «интуиции»: промпты (Qwen-формат, no-think префилл) и парсеры
/// (буквы multichoice, ординальное распределение) — без сети.
/// </summary>
public sealed class IntuitionPromptsTests
{
    // ── Промпты ───────────────────────────────────────────────────────────────

    [Fact]
    public void ChoicePrompt_ContainsQuestionOptionsAndNoThinkPrefill()
    {
        var prompt = IntuitionPrompts.BuildChoicePrompt(
            "Which tool takes screenshots?", new[] { "browser", "desktop", "csharp" });

        Assert.Contains("Which tool takes screenshots?", prompt);
        Assert.Contains("A: browser", prompt);
        Assert.Contains("B: desktop", prompt);
        Assert.Contains("C: csharp", prompt);
        Assert.Contains("D: None of the above", prompt);
        // No-think префилл — в конце (пустой блок размышления перед ответом модели;
        // после открывающего маркера блока — ОДИН \n, см. NoThinkPrefill).
        Assert.EndsWith(
            QwenSpecialTokens.ThinkStart + "\n" + QwenSpecialTokens.ThinkEnd + "\n\n",
            prompt);
        // Роль assistant открыта (префилл идёт от имени ассистента).
        Assert.Contains(QwenSpecialTokens.ImStart + QwenSpecialTokens.Assistant + "\n", prompt);
    }

    [Fact]
    public void ChoicePrompt_OptionsInOrder()
    {
        var prompt = IntuitionPrompts.BuildChoicePrompt("q", new[] { "one", "two" });

        var indexOne = prompt.IndexOf("A: one", StringComparison.Ordinal);
        var indexTwo = prompt.IndexOf("B: two", StringComparison.Ordinal);
        Assert.True(indexOne >= 0 && indexTwo > indexOne);
    }

    [Fact]
    public void RatingPrompt_ContainsStatementAndDigitInstruction()
    {
        var prompt = IntuitionPrompts.BuildRatingPrompt("The build will pass", "0 = will fail, 9 = will pass");

        Assert.Contains("The build will pass", prompt);
        Assert.Contains("0 = will fail, 9 = will pass", prompt);
        Assert.Contains("ONE digit", prompt);
    }

    [Fact]
    public void RatingPrompt_DefaultScaleWhenEmpty()
    {
        var prompt = IntuitionPrompts.BuildRatingPrompt("statement");

        Assert.Contains("0 = no / not at all / very low, 9 = yes / to the maximum extent / very high", prompt);
    }

    [Fact]
    public void VibePrompt_ContainsTextAndEmojiRules()
    {
        var prompt = IntuitionPrompts.BuildVibePrompt("the CI is broken again");

        Assert.Contains("the CI is broken again", prompt);
        Assert.Contains("SINGLE emoji", prompt);
        Assert.EndsWith(
            QwenSpecialTokens.ThinkStart + "\n" + QwenSpecialTokens.ThinkEnd + "\n\n",
            prompt);
    }

    // ── Парсер букв (multichoice) ─────────────────────────────────────────────

    private static ProbeResult Pos(string argmax) =>
        new(argmax, -1.0, Array.Empty<ProbeToken>(), 0.0);

    [Fact]
    public void ParseChoiceLetters_UniqueInOrder()
    {
        var positions = new[] { Pos("B"), Pos("A"), Pos("B") };

        var letters = IntuitionPrompts.ParseChoiceLetters(positions, optionCount: 4);

        Assert.Equal(new[] { 1, 0 }, letters); // B, затем A; повтор B отброшен
    }

    [Fact]
    public void ParseChoiceLetters_IgnoresOutOfRangeAndNonLetters()
    {
        var positions = new[] { Pos("Z"), Pos("x"), Pos(" "), Pos("A") };

        var letters = IntuitionPrompts.ParseChoiceLetters(positions, optionCount: 3);

        Assert.Equal(new[] { 0 }, letters); // из A..C (3 опций + None=D) валидна только A
    }

    [Fact]
    public void ParseChoiceLetters_AllowsNoneLetter()
    {
        var positions = new[] { Pos("C") };

        var letters = IntuitionPrompts.ParseChoiceLetters(positions, optionCount: 3);

        // 2 опции (A,B) + None (C): C — валидная буква-None.
        Assert.Equal(new[] { 2 }, letters);
    }

    // ── Ординальное распределение ─────────────────────────────────────────────

    [Fact]
    public void DigitDistribution_WeightedScoreAndEntropy()
    {
        // Позиция с цифрами: 9 (p=0.5), 8 (p=0.3), 7 (p=0.2)
        var position = new ProbeResult(
            "9", -0.69,
            new[]
            {
                new ProbeToken("9", Math.Log(0.5)),
                new ProbeToken("8", Math.Log(0.3)),
                new ProbeToken("7", Math.Log(0.2)),
            }, 0.0);

        var (score, entropy, dist, found) = IntuitionPrompts.DigitDistribution(new[] { position });

        Assert.True(found);
        Assert.Equal(0.5, dist[9], 3);
        Assert.Equal(0.3, dist[8], 3);
        Assert.Equal(0.2, dist[7], 3);
        Assert.Equal(9 * 0.5 + 8 * 0.3 + 7 * 0.2, score, 3); // 8.3
        Assert.True(entropy > 0 && entropy < Math.Log2(10));
    }

    [Fact]
    public void DigitDistribution_SkipsPositionsWithoutDigits()
    {
        var junk = new ProbeResult(
            "\n", -1.0,
            new[] { new ProbeToken("\n", Math.Log(0.9)), new ProbeToken("the", Math.Log(0.1)) }, 0.0);
        var digits = new ProbeResult(
            "5", -0.7,
            new[] { new ProbeToken("5", Math.Log(1.0)) }, 0.0);

        var (score, _, dist, found) = IntuitionPrompts.DigitDistribution(new[] { junk, digits });

        Assert.True(found);
        Assert.Equal(5.0, score, 3);
        Assert.Equal(1.0, dist[5], 3);
    }

    [Fact]
    public void DigitDistribution_NoDigits_MaximallyUncertain()
    {
        var junk = new ProbeResult(
            "the", -1.0,
            new[] { new ProbeToken("the", Math.Log(0.8)), new ProbeToken("quick", Math.Log(0.2)) }, 0.0);

        var (score, entropy, _, found) = IntuitionPrompts.DigitDistribution(new[] { junk });

        Assert.False(found);
        Assert.Equal(4.5, score, 3);
        Assert.Equal(Math.Log2(10), entropy, 3);
    }

    [Fact]
    public void FirstDigit_HandlesTokenVariants()
    {
        Assert.Equal(9, IntuitionPrompts.FirstDigit("9"));
        Assert.Equal(7, IntuitionPrompts.FirstDigit(" 7"));
        Assert.Equal(0, IntuitionPrompts.FirstDigit("0x1F"));
        Assert.Null(IntuitionPrompts.FirstDigit("abc"));
        Assert.Null(IntuitionPrompts.FirstDigit(""));
        Assert.Null(IntuitionPrompts.FirstDigit(null!));
    }
}
