using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Probes;

namespace QwenPlayground.Core.Tests;

public sealed class MemoryClassifierTests
{
    [Fact]
    public void AccumulateLayers_BuildsNormalizedDistribution()
    {
        var positions = new List<ProbeResult>
        {
            BuildPosition([("A", -0.1), ("B", -1.0), ("C", -2.0)]),
            BuildPosition([("A", -0.3), ("C", -1.5)])
        };

        var layers = MemoryClassifier.AccumulateLayers(positions, MemoryCategories.IsCategoryLetter);

        Assert.Contains("A", layers);
        Assert.Contains("B", layers);
        Assert.Contains("C", layers);
        Assert.Equal(1.0, layers.Values.Sum(), 6);
        Assert.True(layers["A"] > layers["C"], "A доминирует на обеих позициях");
    }

    [Fact]
    public void AccumulateLayers_SkipsDisallowedTokens()
    {
        var positions = new List<ProbeResult>
        {
            BuildPosition([("A", -0.1), ("word", -0.5), (" ", -1.0)])
        };

        var layers = MemoryClassifier.AccumulateLayers(positions, MemoryCategories.IsCategoryLetter);

        Assert.Single(layers);
        Assert.Contains("A", layers);
    }

    [Fact]
    public void BuildCategoryPrompt_ListsAllCategories()
    {
        var prompt = MemoryClassifier.BuildCategoryPrompt("некий текст");

        Assert.Contains("A: code", prompt);
        Assert.Contains("Z: process", prompt);
        Assert.Contains("некий текст", prompt);
    }

    [Fact]
    public void AccumulateLayers_TrimsNativeTokensWithLeadingSpaces()
    {
        // Нативный /completion отдаёт токены с ведущими пробелами (" I") — они должны
        // склеиться с обычными "I", а мультибуквенные ("AD") отфильтроваться.
        var positions = new List<ProbeResult>
        {
            BuildPosition([(" I", -0.3), ("AD", -1.0)]),
            BuildPosition([("I", -0.2), ("A", -1.5)])
        };

        var layers = MemoryClassifier.AccumulateLayers(positions, MemoryCategories.IsCategoryLetter);

        Assert.Equal(2, layers.Count);
        Assert.Contains("I", layers);
        Assert.Contains("A", layers);
        Assert.False(layers.ContainsKey("AD"), "мультибуквенный токен отфильтрован");
        Assert.True(layers["I"] > layers["A"], "I доминирует — токен с ведущим пробелом склеился с обычным");
        Assert.Equal(1.0, layers.Values.Sum(), 6);
    }

    [Fact]
    public void BuildCategoryPrompt_IsNekoBotTurnFormat()
    {
        var prompt = MemoryClassifier.BuildCategoryPrompt("некий текст");

        Assert.Contains("<|turn>system", prompt);
        Assert.Contains("<|turn>user", prompt);
        Assert.EndsWith("<|turn>model\n", prompt);
        Assert.Contains("<turn|>", prompt);
        Assert.Contains("Answer ONLY with related char to category", prompt);
        Assert.Contains("некий текст", prompt);
    }

    [Fact]
    public void IsEmojiToken_RejectsTextAndAcceptsEmoji()
    {
        Assert.False(MemoryCategories.IsEmojiToken("word"));
        Assert.False(MemoryCategories.IsEmojiToken(" "));
        Assert.True(MemoryCategories.IsEmojiToken("🔥"));
        Assert.True(MemoryCategories.IsEmojiToken("💻"));
    }

    [Fact]
    public void IsEmojiToken_RejectsLoneVariationSelector()
    {
        // Gemma отвечает отдельным токеном U+FE0F без базового эмодзи — это не эмодзи.
        Assert.False(MemoryCategories.IsEmojiToken("\uFE0F"));
        Assert.False(MemoryCategories.IsEmojiToken("\uFE0F\uFE0F"));
        // Базовый эмодзи с вариацией — эмодзи.
        Assert.True(MemoryCategories.IsEmojiToken("\u2764\uFE0F"));
    }

    [Fact]
    public void IsEmojiToken_RejectsSplitEmojiToken()
    {
        // Эмодзи, разбитый токенизатором на два токена, оставляет непарный суррогат —
        // вне эмодзи-диапазонов → токен игнорируется (как в NekoBot).
        Assert.False(MemoryCategories.IsEmojiToken("\uD83D"));      // старший суррогат без пары
        Assert.False(MemoryCategories.IsEmojiToken("\uDE25"));      // младший суррогат
        Assert.True(MemoryCategories.IsEmojiToken("\uD83D\uDE25")); // целая пара 😅
    }

    [Fact]
    public void ParseRerankLetters_UniqueOrderedAndSkipsOutOfRange()
    {
        var positions = new List<ProbeResult>
        {
            BuildPosition([("B", -0.1), ("A", -0.2)]),
            BuildPosition([("D", -0.3)]),
            BuildPosition([("B", -0.4)]),
            BuildPosition([("Z", -0.5)]),
            BuildPosition([("AA", -0.6)])
        };

        // 3 кандидата + None = 4 опции (A-D); Z и AA вне диапазона.
        var chosen = MemoryClassifier.ParseRerankLetters(positions, optionCount: 4);

        Assert.Equal(new[] { 1, 3 }, chosen); // B, D — уникальные, в порядке появления
    }

    [Fact]
    public void ParseRerankLetters_NoneIndex()
    {
        var positions = new List<ProbeResult>
        {
            BuildPosition([("D", -0.1)]), // D = индекс 3 = None при 3 кандидатах + None
        };

        var chosen = MemoryClassifier.ParseRerankLetters(positions, optionCount: 4);

        Assert.Equal(new[] { 3 }, chosen); // None — последняя опция
    }

    [Fact]
    public void BuildRerankPrompt_ListsCandidatesWithNoneOption()
    {
        var candidates = new List<MemoryHit>
        {
            new(new MemoryItem { Content = "первый факт" }, 0.5),
            new(new MemoryItem { Content = "второй факт" }, 0.3),
        };

        var prompt = MemoryClassifier.BuildRerankPrompt("диалог про память", candidates);

        Assert.Contains("<|turn>system", prompt);
        Assert.Contains("A: первый факт", prompt);
        Assert.Contains("B: второй факт", prompt);
        Assert.Contains("C: None of the above", prompt);
        Assert.Contains("Use ONLY (A-C) chars", prompt);
    }

    [Fact]
    public void FlushTargets_PicksStaleAndLayerlessOldestFirst()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qwen_flush_test_" + Guid.NewGuid().ToString("N"));
        var store = new MemoryStore(dir);
        try
        {
            // Актуальный: слои + текущая версия → не цель.
            var fresh = store.Add("свежий факт");
            fresh.CategoryLayers["A"] = 1.0;
            fresh.LayersVersion = MemoryClassifier.CurrentLayerVersion;
            store.Update(fresh);

            // Без слоёв → цель.
            var layerless = store.Add("факт без вектора");
            // Старая версия (слои «затёрты» словарём) → цель.
            var stale = store.Add("факт со старым словарём");
            stale.CategoryLayers["A"] = 1.0;
            stale.LayersVersion = 0;
            store.Update(stale);

            var targets = MemoryClassifier.FlushTargets(store, budget: 2);

            Assert.Equal(2, targets.Count);
            Assert.Contains(layerless.Id, targets.Select(t => t.Id));
            Assert.Contains(stale.Id, targets.Select(t => t.Id));
            Assert.DoesNotContain(fresh.Id, targets.Select(t => t.Id));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static ProbeResult BuildPosition((string Token, double LogProb)[] tokens)
    {
        var list = tokens.Select(t => new ProbeToken(t.Token, t.LogProb)).ToList();
        return new ProbeResult(tokens[0].Token, tokens[0].LogProb, list, 0.5);
    }
}
