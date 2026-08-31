using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Probes;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Проход сканера дубликатов на скриптованной пробе (без сети): бюджет, вердикты,
/// пропуск уже стоящих в очереди пар.
/// </summary>
public sealed class MemorySimilarityScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qpw_scan_" + Guid.NewGuid().ToString("N"));

    private static ProbeResult Position(char digit, double prob) =>
        new(digit.ToString(), double.NegativeInfinity,
            [new ProbeToken(digit.ToString(), Math.Log(prob)), new ProbeToken("\n", Math.Log(1 - prob))],
            Entropy: 0);

    private static MemoryStore StoreWith(params string[] contents)
    {
        var store = new MemoryStore(Path.Combine(Path.GetTempPath(), "qpw_scan_store_" + Guid.NewGuid().ToString("N")));
        foreach (var content in contents)
        {
            var item = store.Add(content);
            // Сканер работает только по фактам со слоями (инвариант: классификация догнала).
            item.CategoryLayers = new Dictionary<string, double> { ["A"] = 1.0 };
            item.EmojiLayers = new Dictionary<string, double> { ["🙂"] = 1.0 };
            store.Update(item);
        }
        return store;
    }

    /// <summary>Проба возвращает цифру по номеру вызова (порядок кандидатов детерминирован).</summary>
    private static Func<string, string, string, CancellationToken, Task<ProbeResult>> Scripted(params char[] digits)
    {
        var i = 0;
        return (prompt, refId, candId, ct) => Task.FromResult(Position(digits[Math.Min(i++, digits.Length - 1)], 0.99));
    }

    [Fact]
    public async Task ScanPass_RespectsBudget_AndQueuesUncertain()
    {
        var store = StoreWith("a", "b", "c", "d");
        var pairs = new PairsStore(_root);

        var report = await MemorySimilarity.ScanPassAsync(
            store, pairs, "http://x", probeBudget: 2,
            Scripted('8', '8'), CancellationToken.None); // уверенно «похожи» (score≈8 ≥ 7.5)

        Assert.Equal(2, report.Probes);
        Assert.Equal(2, report.QueuedSimilar);
        Assert.Equal(2, pairs.Pending.Count); // очередь копится, сканер не остановился
    }

    [Fact]
    public async Task ScanPass_ConfidentDistinct_MarksImmediately()
    {
        var store = StoreWith("дубликат про деплой", "копия про деплой", "совсем другое");
        var pairs = new PairsStore(_root);

        var report = await MemorySimilarity.ScanPassAsync(
            store, pairs, "http://x", probeBudget: 5,
            Scripted('1', '1'), CancellationToken.None);

        Assert.True(report.MarkedDistinct > 0);
        Assert.Empty(pairs.Pending); // разведённые в очередь не попадают
    }

    [Fact]
    public async Task ScanPass_DoesNotReprobe_AlreadyPending()
    {
        var store = StoreWith("a", "b", "c");
        var pairs = new PairsStore(_root);
        // Все три пары уже в очереди — сканеру нечего пробовать, бюджет не тратится.
        var ids = store.List().Select(m => m.Id).ToList(); // новые → старые
        pairs.AddPending(ids[2], ids[1]);
        pairs.AddPending(ids[2], ids[0]);
        pairs.AddPending(ids[1], ids[0]);
        var probed = 0;

        var report = await MemorySimilarity.ScanPassAsync(
            store, pairs, "http://x", probeBudget: 10,
            (prompt, a, b, ct) => { probed++; return Task.FromResult(Position('5', 0.99)); },
            CancellationToken.None);

        Assert.Equal(0, probed);
        Assert.Equal(0, report.Probes);
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
