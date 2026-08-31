using QwenPlayground.Core.Memory;

namespace QwenPlayground.Core.Tests;

public sealed class MemoryExtractorTests
{
    [Fact]
    public void ParseFacts_CleanArray()
    {
        var facts = MemoryExtractor.ParseFacts("""["факт один","факт два"]""");

        Assert.Equal(new[] { "факт один", "факт два" }, facts);
    }

    [Fact]
    public void ParseFacts_MarkdownFences()
    {
        var output = "Вот извлечённые факты:\n```json\n[\"факт в ограждениях\"]\n```\nГотово.";

        var facts = MemoryExtractor.ParseFacts(output);

        Assert.Equal(new[] { "факт в ограждениях" }, facts);
    }

    [Fact]
    public void ParseFacts_EmptyArrayAndGarbage_ReturnEmpty()
    {
        Assert.Empty(MemoryExtractor.ParseFacts("[]"));
        Assert.Empty(MemoryExtractor.ParseFacts("ничего похожего на массив"));
        Assert.Empty(MemoryExtractor.ParseFacts(null!));
    }

    [Fact]
    public void ParseFacts_SkipsNonStringsAndBlanks()
    {
        var facts = MemoryExtractor.ParseFacts("""["факт", 42, null, "   ", "ещё факт"]""");

        Assert.Equal(new[] { "факт", "ещё факт" }, facts);
    }

    [Fact]
    public void BuildExtractionPrompt_ContainsTranscriptAndRules()
    {
        var prompt = MemoryExtractor.BuildExtractionPrompt("### user\nсделай файл");

        Assert.Contains("### user\nсделай файл", prompt);
        Assert.Contains("JSON array", prompt);
        Assert.Contains(MemoryExtractor.MaxFacts.ToString(), prompt);
    }
}
