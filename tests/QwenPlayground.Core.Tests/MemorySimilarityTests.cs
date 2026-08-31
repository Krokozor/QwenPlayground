using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Probes;

namespace QwenPlayground.Core.Tests;

public sealed class MemorySimilarityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qpw_pairs_" + Guid.NewGuid().ToString("N"));

    private static ProbeResult Position(params (string Token, double Prob)[] top) =>
        new("?", double.NegativeInfinity,
            top.Select(t => new ProbeToken(t.Token, Math.Log(t.Prob))).ToList(),
            Entropy: 0);

    private static MemoryItem Item(string id, Dictionary<string, double> categories) =>
        new() { Id = id, Content = id, CategoryLayers = categories, EmojiLayers = new() { ["🙂"] = 1.0 } };

    [Theory]
    [InlineData(new[] { '9' }, 0.98, MemorySimilarity.Verdict.Similar)]
    [InlineData(new[] { '0' }, 0.97, MemorySimilarity.Verdict.Distinct)]
    [InlineData(new[] { '5' }, 0.99, MemorySimilarity.Verdict.Uncertain)] // середина шкалы
    public void Judge_ConfidentDigit_MapsToVerdict(char[] digits, double prob, MemorySimilarity.Verdict expected)
    {
        var position = Position(digits.Select(d => (d.ToString(), prob)).Append(("\n", 1 - prob)).ToArray());
        var judgement = MemorySimilarity.Judge(position, similarMin: 7, distinctMax: 3, confidentMaxEntropy: 2.5);

        Assert.Equal(expected, judgement.Kind);
        Assert.True(judgement.Entropy <= 1.0);
    }

    [Fact]
    public void Judge_SpreadDistribution_IsUncertain_EvenWithHighScore()
    {
        // Равномерно по 5..9: балл 7.0 (>= similarMin), но энтропия log2(5)≈2.32 > порога 2.0 — Uncertain.
        var position = Position(("5", .2), ("6", .2), ("7", .2), ("8", .2), ("9", .2));
        var judgement = MemorySimilarity.Judge(position, similarMin: 7, distinctMax: 3, confidentMaxEntropy: 2.0);

        Assert.Equal(MemorySimilarity.Verdict.Uncertain, judgement.Kind);
        Assert.True(judgement.Entropy > 2.0);
        Assert.InRange(judgement.Score, 6.9, 7.1);
    }

    [Fact]
    public void Judge_NoDigitsInTop_Uncertain()
    {
        var position = Position(("\n", .9), (" ", .1));

        Assert.Equal(MemorySimilarity.Verdict.Uncertain, MemorySimilarity.Judge(position).Kind);
    }

    [Fact]
    public void Prompt_AsksForSingleDigit_ContainsBothMemories()
    {
        var prompt = MemorySimilarity.BuildPairPrompt("факт А", "факт Б");

        Assert.Contains("ONE digit", prompt);
        Assert.Contains("факт А", prompt);
        Assert.Contains("факт Б", prompt);
    }

    [Fact]
    public void CandidatesFor_SortedByOverlap_ExcludesDistinctAndSelf()
    {
        var pairs = new PairsStore(_root);
        var reference = Item("ref", new Dictionary<string, double> { ["A"] = 1.0 });
        var strong = Item("strong", new Dictionary<string, double> { ["A"] = 0.9 });
        var weak = Item("weak", new Dictionary<string, double> { ["B"] = 1.0 });
        var distinct = Item("distinct", new Dictionary<string, double> { ["A"] = 0.8 });
        pairs.MarkDistinct(reference.Id, distinct.Id);
        var others = new[] { weak, distinct, strong };

        var candidates = MemorySimilarity.CandidatesFor(reference, others, pairs);

        // distinct исключён (разведён), сильный кандидат впереди слабого.
        Assert.Equal(["strong", "weak"], candidates.Select(c => c.Id));
    }

    [Fact]
    public void PairsStore_PersistsAcrossInstances_OrderAgnostic()
    {
        var store = new PairsStore(_root);
        store.MarkDistinct("b", "a"); // ключ нормализуется
        store.AddPending("c", "a");

        var fresh = new PairsStore(_root); // новый экземпляр над тем же каталогом

        Assert.True(fresh.IsDistinct("a", "b"));
        Assert.True(fresh.IsDistinct("b", "a"));
        var pending = Assert.Single(fresh.Pending);
        Assert.Equal("c", pending.A);
        Assert.Equal("a", pending.B);
    }

    [Fact]
    public void AddPending_IgnoresDistinctAndDuplicates()
    {
        var store = new PairsStore(_root);
        store.MarkDistinct("x", "y");

        store.AddPending("x", "y"); // разведённую пару в очередь не берём
        store.AddPending("x", "y"); // и дубликат тоже

        Assert.Empty(store.Pending);
    }

    [Fact]
    public void Cleanup_RemovesPairsReferencingDeadIds()
    {
        var store = new PairsStore(_root);
        store.MarkDistinct("dead1", "alive");
        store.AddPending("alive", "dead2");

        store.Cleanup(["alive"]);

        Assert.False(store.IsDistinct("dead1", "alive"));
        Assert.Empty(store.Pending);

        var fresh = new PairsStore(_root);
        Assert.False(fresh.IsDistinct("dead1", "alive")); // чистка персистентна
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
