using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Memory;

namespace QwenPlayground.Core.Tests;

public sealed class MemoryLayerPipelineTests
{
    private static Func<string, CancellationToken, Task<string>> FakeCompleter(
        Dictionary<string, string> responses,
        List<string>? prompts = null,
        Func<string, bool>? throwFor = null) =>
        async (prompt, _) =>
        {
            prompts?.Add(prompt);
            if (throwFor?.Invoke(prompt) == true)
            {
                throw new InvalidOperationException("fake failure");
            }
            foreach (var (needle, response) in responses)
            {
                if (prompt.Contains(needle))
                {
                    return response;
                }
            }
            return "fallback";
        };

    [Fact]
    public async Task RunAsync_HappyPath_RotatesLayersAndCollectsFacts()
    {
        var current = new LayerMemory { L1 = "old L1", L2 = "old L2", L3 = "old L3" };
        const string transcript = "### user\nsome chat";
        var prompts = new List<string>();

        var result = await MemoryLayerPipeline.RunAsync(current, transcript, FakeCompleter(
            new Dictionary<string, string>
            {
                ["Merge them into one dense layer"] = "merged temp",
                ["verify fact loss during memory merging"] = "[\"fact merge 1\", \"fact merge 2\"]",
                ["You summarize a segment"] = "segment summary",
                ["verify fact loss during summarization"] = "[\"fact seg 1\"]"
            }, prompts));

        Assert.True(result.MergeSucceeded);
        Assert.True(result.SegmentSucceeded);
        Assert.Equal("merged temp", result.Next.L1);
        Assert.Equal("old L3", result.Next.L2); // старый L3 сдвинулся в L2
        Assert.Equal("segment summary", result.Next.L3);
        Assert.Equal(new[] { "fact merge 1", "fact merge 2", "fact seg 1" }, result.Facts);
        Assert.Equal(4, prompts.Count); // ровно 4 изолированных вызова
    }

    [Fact]
    public async Task RunAsync_IsolatesContexts()
    {
        const string l1 = "SECRET_L1";
        const string transcript = "SECRET_TRANSCRIPT";
        var prompts = new List<string>();

        await MemoryLayerPipeline.RunAsync(
            new LayerMemory { L1 = l1, L2 = "l2" },
            transcript,
            FakeCompleter(new Dictionary<string, string> { ["anything"] = "ok" }, prompts));

        var mergePrompt = prompts[0];
        var segmentPrompt = prompts[2];

        // Мердж видит только слои, не транскрипт.
        Assert.Contains(l1, mergePrompt);
        Assert.DoesNotContain(transcript, mergePrompt);
        // Суммаризация сегмента видит только транскрипт, не слои.
        Assert.Contains(transcript, segmentPrompt);
        Assert.DoesNotContain(l1, segmentPrompt);
    }

    [Fact]
    public async Task RunAsync_MergeFails_KeepsAllThreeLayers()
    {
        var current = new LayerMemory { L1 = "old L1", L2 = "old L2", L3 = "old L3" };

        var result = await MemoryLayerPipeline.RunAsync(current, "transcript", FakeCompleter(
            new Dictionary<string, string> { ["You summarize a segment"] = "segment summary" },
            throwFor: p => p.Contains("Merge them into one dense layer")));

        Assert.False(result.MergeSucceeded);
        Assert.True(result.SegmentSucceeded);
        // Сбой merge делает каскад невозможным: ни один слой не сдвинут, старый L3 не перетёрт.
        Assert.Equal("old L1", result.Next.L1);
        Assert.Equal("old L2", result.Next.L2);
        Assert.Equal("old L3", result.Next.L3);
        Assert.Empty(result.Facts); // валидация мерджа не запускалась
    }

    [Fact]
    public async Task RunAsync_SegmentFails_NoDuplication_KeepsL2AndL3()
    {
        var current = new LayerMemory { L1 = "old L1", L2 = "middle L2", L3 = "old L3" };

        var result = await MemoryLayerPipeline.RunAsync(current, "transcript", FakeCompleter(
            new Dictionary<string, string> { ["Merge them into one dense layer"] = "merged temp" },
            throwFor: p => p.Contains("You summarize a segment")));

        Assert.True(result.MergeSucceeded);
        Assert.False(result.SegmentSucceeded);
        // Без нового L3 ротация не применяется вовсе: слитый Temp в L1 не пишется (иначе L2 теряется),
        // старый L3 на месте, дублей нет.
        Assert.Equal("old L1", result.Next.L1);
        Assert.Equal("middle L2", result.Next.L2);
        Assert.Equal("old L3", result.Next.L3);
    }

    [Fact]
    public async Task RunAsync_WarmStart_NoL1_ShiftsWithoutMerge()
    {
        // Тёплый ап-фейз: только L2 и L3, L1 пуст → слияние НЕ запускается,
        // каскадный сдвиг дословно: L2→L1, L3→L2, новый сегмент→L3.
        var current = new LayerMemory { L2 = "old L2", L3 = "old L3" };
        var prompts = new List<string>();

        var result = await MemoryLayerPipeline.RunAsync(current, "transcript", FakeCompleter(
            new Dictionary<string, string> { ["You summarize a segment"] = "segment summary" }, prompts));

        Assert.True(result.MergeSucceeded); // тривиально: слияния не было
        Assert.True(result.SegmentSucceeded);
        Assert.Equal("old L2", result.Next.L1);
        Assert.Equal("old L3", result.Next.L2);
        Assert.Equal("segment summary", result.Next.L3);
        Assert.DoesNotContain(prompts, p => p.Contains("Merge them into one dense layer"));
        Assert.DoesNotContain(prompts, p => p.Contains("verify fact loss during memory merging"));
        Assert.Equal(2, prompts.Count); // суммаризация сегмента + его валидация
    }

    [Fact]
    public async Task RunAsync_EmptyLayers_OnlySummarizesSegment()
    {
        var prompts = new List<string>();

        var result = await MemoryLayerPipeline.RunAsync(
            new LayerMemory(),
            "transcript",
            FakeCompleter(new Dictionary<string, string> { ["You summarize a segment"] = "segment summary" }, prompts));

        Assert.True(result.MergeSucceeded); // тривиально: сливать нечего
        Assert.True(result.SegmentSucceeded);
        Assert.Equal(string.Empty, result.Next.L1);
        Assert.Equal(string.Empty, result.Next.L2);
        Assert.Equal("segment summary", result.Next.L3);
        Assert.Equal(2, prompts.Count); // суммаризация сегмента + его валидация
        Assert.All(prompts, p => Assert.DoesNotContain("Merge them into one dense layer", p));
    }

    [Fact]
    public async Task RunAsync_ValidationReturnsGarbage_NoFacts()
    {
        var result = await MemoryLayerPipeline.RunAsync(
            new LayerMemory { L1 = "old L1", L3 = "old L3" },
            "transcript",
            FakeCompleter(new Dictionary<string, string>
            {
                ["Merge them into one dense layer"] = "merged temp",
                ["You summarize a segment"] = "segment summary",
                ["verify fact loss"] = "не массив, просто текст"
            }));

        Assert.Empty(result.Facts);
        Assert.True(result.MergeSucceeded);
        Assert.True(result.SegmentSucceeded);
    }
}