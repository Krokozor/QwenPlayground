using System.IO;
using QwenPlayground.Core.Memory;

namespace QwenPlayground.Core.Tests;

public sealed class MemoryRecallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qpw_recall_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NormalizedOverlap_IsMinSum_NormalizedByQuery()
    {
        var query = new Dictionary<string, double> { ["A"] = 0.6, ["B"] = 0.4 };
        var memory = new Dictionary<string, double> { ["A"] = 0.9, ["C"] = 0.5 };

        var score = MemoryRecall.NormalizedOverlap(query, memory);

        Assert.Equal(0.6, score, 6); // min(0.6,0.9)=0.6 / (0.6+0.4)=1.0
    }

    [Fact]
    public void ScoreSemantic_WeightsCategoriesHigherThanEmoji()
    {
        const double cw = 0.7, ew = 0.3;
        var categories = new Dictionary<string, double> { ["A"] = 1.0 };
        var categoriesOnly = MemoryRecall.ScoreSemantic(
            new MemorySemanticLayers { Categories = new() { ["A"] = 1.0 } }, categories, new Dictionary<string, double>(),
            categoryWeight: cw, emojiWeight: ew);

        var both = MemoryRecall.ScoreSemantic(
            new MemorySemanticLayers { Categories = new() { ["A"] = 1.0 }, Emoji = new() { ["🔥"] = 1.0 } },
            categories, new Dictionary<string, double> { ["🔥"] = 1.0 },
            categoryWeight: cw, emojiWeight: ew);

        Assert.Equal(cw, categoriesOnly, 6);
        Assert.Equal(cw + ew, both, 6);
        Assert.True(both > categoriesOnly);
    }

    [Fact]
    public void ScoreText_UsesDiceCoefficient()
    {
        Assert.Equal(1.0, MemoryRecall.ScoreText("foo bar", "bar foo"), 6);
        Assert.Equal(0.0, MemoryRecall.ScoreText("foo", "bar baz"), 6);
        Assert.True(MemoryRecall.ScoreText("персистентность памяти", "персистентность и память") > 0.2);
    }

    [Fact]
    public async Task RecallAsync_ProbeDown_FallsBackToTextOverlap()
    {
        var store = new MemoryStore(_dir);
        store.Add("foo bar baz", source: "compaction");

        // Порт 1 — соединение отбрасывается мгновенно; ClassifyAsync фолбэчится на пустые слои,
        // реколл скорит по тексту.
        var hits = await MemoryRecall.RecallAsync("foo", store, "http://127.0.0.1:1", topX: 3, minScore: 0.05);

        Assert.Single(hits);
        Assert.Equal("foo bar baz", hits[0].Item.Content);
    }

    [Fact]
    public async Task RecallAsync_ReturnsTopX_OrderedByScore()
    {
        var store = new MemoryStore(_dir);
        store.Add("alpha beta gamma", source: "compaction");
        store.Add("delta epsilon", source: "compaction");
        store.Add("omega", source: "compaction");

        var hits = await MemoryRecall.RecallAsync("alpha beta", store, "http://127.0.0.1:1", topX: 2, minScore: 0.0);

        Assert.Equal(2, hits.Count);
        Assert.Equal("alpha beta gamma", hits[0].Item.Content);
    }

    [Fact]
    public async Task RecallAsync_RerankProbeDown_FallsBackToPass1()
    {
        var store = new MemoryStore(_dir);
        store.Add("foo bar baz", source: "compaction");

        // Порт 1 отказывает мгновенно: rerank (SecondPass) падает → откат к pass 1.
        var hits = await MemoryRecall.RecallAsync(
            "foo", store, "http://127.0.0.1:1", topX: 3, minScore: 0.05, rerank: true);

        Assert.Single(hits);
        Assert.Equal("foo bar baz", hits[0].Item.Content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
