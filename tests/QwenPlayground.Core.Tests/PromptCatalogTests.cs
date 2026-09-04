using QwenPlayground.Core.Compaction;

namespace QwenPlayground.Core.Tests;

public sealed class PromptCatalogTests
{
    [Fact]
    public void Load_ReturnsDefaults_WhenFileMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "prompts.json");

        var set = PromptCatalog.Load(path);

        Assert.Equal(PromptCatalog.Defaults.Merge, set.Merge);
        Assert.Equal(PromptCatalog.Defaults.SegmentSummary, set.SegmentSummary);
        Assert.Contains("{{transcript}}", set.SegmentSummary);
    }

    [Fact]
    public void Save_And_Load_Roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "prompts.json");
        var set = new PromptTemplateSet
        {
            Merge = "custom merge",
            SegmentSummary = "custom segment",
            MemoryExtraction = "extract {{max_facts}} from {{transcript}}"
        };

        PromptCatalog.Save(set, path);
        var loaded = PromptCatalog.Load(path);

        Assert.Equal("custom merge", loaded.Merge);
        Assert.Equal("custom segment", loaded.SegmentSummary);
        Assert.Equal("extract {{max_facts}} from {{transcript}}", loaded.MemoryExtraction);
        // Неизменённые поля остаются дефолтами.
        Assert.Equal(PromptCatalog.Defaults.SegmentValidation, loaded.SegmentValidation);
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

    [Fact]
    public void SegmentSummary_Default_RendersLayerTemplate()
    {
        var prompt = PromptCatalog.Defaults.SegmentSummary;

        // Скелет секций — с начала строки (отступ raw-строки снят рендером).
        Assert.Contains("\n## Задача\n", prompt);
        Assert.Contains("\n## Контекст\n", prompt);
        Assert.Contains("\n## Решения\n", prompt);
        Assert.Contains("\n## Ошибки и инциденты\n", prompt);
        Assert.Contains("\n## Состояние\n", prompt);
        Assert.Contains("\n### Готово\n", prompt);
        Assert.Contains("\n### В работе\n", prompt);
        Assert.Contains("\n### Заблокировано\n", prompt);
        Assert.Contains("\n## Открытые нити\n", prompt);
        Assert.Contains("\n## Дальше\n", prompt);
        Assert.Contains("\n## Файлы\n", prompt);
        Assert.Contains("<template>", prompt);
        Assert.Contains("</template>", prompt);
        Assert.Contains("{{transcript}}", prompt);
    }

    [Fact]
    public void Merge_Default_SharesLayerTemplate_AndChronology()
    {
        var merge = PromptCatalog.Defaults.Merge;

        // Тот же скелет, что у сегмента: слой — один сорт документа.
        Assert.Contains("\n## Задача\n", merge);
        Assert.Contains("\n## Файлы\n", merge);
        // Хронология (L2 новее), дельта инлайн, структура сохраняется.
        Assert.Contains("chronologically later", merge);
        Assert.Contains("delta inline", merge);
        Assert.Contains("same structure", merge);
        Assert.Contains("{{l1}}", merge);
        Assert.Contains("{{l2}}", merge);
    }
}