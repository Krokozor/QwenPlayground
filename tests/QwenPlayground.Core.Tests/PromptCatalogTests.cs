using QwenPlayground.Core.Compaction;

namespace QwenPlayground.Core.Tests;

public sealed class PromptCatalogTests
{
    [Fact]
    public void Load_ReturnsDefaults_WhenFileMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "prompts.json");

        var set = PromptCatalog.Load(path);

        Assert.Equal(PromptCatalog.Defaults.SummarizationSystem, set.SummarizationSystem);
        Assert.Equal(PromptCatalog.Defaults.Merge, set.Merge);
        Assert.Contains("{{transcript}}", set.SummarizationUser);
    }

    [Fact]
    public void Save_And_Load_Roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "prompts.json");
        var set = new PromptTemplateSet
        {
            SummarizationSystem = "custom system",
            SegmentSummary = "custom segment",
            MemoryExtraction = "extract {{max_facts}} from {{transcript}}"
        };

        PromptCatalog.Save(set, path);
        var loaded = PromptCatalog.Load(path);

        Assert.Equal("custom system", loaded.SummarizationSystem);
        Assert.Equal("custom segment", loaded.SegmentSummary);
        Assert.Equal("extract {{max_facts}} from {{transcript}}", loaded.MemoryExtraction);
        // Неизменённые поля остаются дефолтами.
        Assert.Equal(PromptCatalog.Defaults.Merge, loaded.Merge);
    }

    [Fact]
    public void Render_SubstitutesPlaceholders_AndTrims()
    {
        var template = "два слоя:\n{{l1}}\n{{l2}}\nхвост";

        var result = PromptTemplateSet.Render(template, new Dictionary<string, string>
        {
            ["l1"] = "  первый  ",
            ["l2"] = "второй"
        });

        Assert.Equal("два слоя:\n  первый  \nвторой\nхвост", result);
    }

    [Fact]
    public void Render_UnknownPlaceholder_StaysIntact()
    {
        var result = PromptTemplateSet.Render("keep {{nothing}}", new Dictionary<string, string>());

        Assert.Equal("keep {{nothing}}", result);
    }
}