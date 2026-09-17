using QwenPlayground.Core.Roslyn;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// CSharpReferenceReportTool — пакетный репорт счётчиков ссылок (CodeLens одним
/// вызовом). Тесты против живого солюшена (паттерн RoslynServiceTests): режимы,
/// фильтры, детальный отчёт в файл.
/// </summary>
public sealed class CSharpReferenceReportToolTests
{
    private static ToolContext TestContext() => new(Path.GetTempPath());

    [Fact]
    public async Task Members_ReportShowsStructureAndKnownMember()
    {
        var tool = new CSharpReferenceReportTool { Type = "QwenChatTemplate" };
        var result = await tool.ExecuteAsync(TestContext(), CancellationToken.None);

        Assert.DoesNotContain("Ambiguous", result);
        Assert.Contains("reference report: QwenChatTemplate", result);
        Assert.Contains("summary:", result);
        Assert.Contains("caveats:", result);
        Assert.Contains("Render", result); // известный живой метод
    }

    [Fact]
    public async Task Members_MaxRefsFilter_OnlyShowsLowRefMembers()
    {
        var tool = new CSharpReferenceReportTool { Type = "QwenChatTemplate", MaxRefs = 0 };
        var result = await tool.ExecuteAsync(TestContext(), CancellationToken.None);

        // Каждая строка таблицы — 0 ссылок (или пустая таблица, если мёртвых нет).
        var lines = result.Split('\n');
        var dataLines = lines
            .Where(l => l.Length > 5 && int.TryParse(l[..4].Trim(), out _))
            .Where(l => l.Contains("method") || l.Contains("property") || l.Contains("field")
                        || l.Contains("ctor") || l.Contains("event"));
        foreach (var line in dataLines)
        {
            Assert.Equal(0, int.Parse(line[..4].Trim()));
        }
    }

    [Fact]
    public async Task Members_MissingType_ReturnsNotFound()
    {
        var tool = new CSharpReferenceReportTool { Type = "NoSuchType_XYZ_123" };
        var result = await tool.ExecuteAsync(TestContext(), CancellationToken.None);

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task Types_Mode_ListsTypesOfNamespace()
    {
        var tool = new CSharpReferenceReportTool { Mode = "types", Namespace = "QwenPlayground.Core.Roslyn" };
        var result = await tool.ExecuteAsync(TestContext(), CancellationToken.None);

        Assert.Contains("types —", result);
        Assert.Contains("CSharpReferencesTool", result);
        Assert.Contains("CSharpReferenceReportTool", result);
    }

    [Fact]
    public async Task Types_XamlElementTagCountsAsReference()
    {
        // BoolToVisibilityConverter живёт ТОЛЬКО в XAML (<views:BoolToVisibilityConverter/>) —
        // C#-ссылок нет, XAML-индекс должен дать refs > 0 с разбивкой в note.
        var tool = new CSharpReferenceReportTool
        {
            Mode = "types",
            File = "src/QwenPlayground.App/Views/DiagnosticsConverters.cs",
        };
        var result = await tool.ExecuteAsync(TestContext(), CancellationToken.None);

        Assert.DoesNotContain("not found", result);
        // Строка данных (а не заголовок, где имя встречается в file=).
        var line = result.Split('\n')
            .First(l => l.Contains("BoolToVisibilityConverter") && int.TryParse(l[..4].Trim(), out _));
        Assert.True(int.Parse(line[..4].Trim()) > 0, $"ожидается refs > 0, строка: {line}");
        Assert.Contains("xaml:", result);
    }

    [Fact]
    public async Task Members_XamlBindingCountsAsReference()
    {
        // DiagnosticsViewModel мелкий и весь прикручен к DiagnosticsView.xaml биндингами.
        var tool = new CSharpReferenceReportTool { Type = "DiagnosticsViewModel" };
        var result = await tool.ExecuteAsync(TestContext(), CancellationToken.None);

        Assert.DoesNotContain("not found", result);
        Assert.Contains("xaml:", result); // хотя бы один член живёт биндингом
    }

    [Fact]
    public async Task Members_AccessorsAndBackingFieldFolded()
    {
        var tool = new CSharpReferenceReportTool { Type = "DiagnosticsViewModel" };
        var result = await tool.ExecuteAsync(TestContext(), CancellationToken.None);

        Assert.DoesNotContain("k__BackingField", result);
        // Аксессоры не строчатся отдельно: get_X встречается только как часть note/first use.
        var accessorLines = result.Split('\n')
            .Where(l => l.Length > 5 && int.TryParse(l[..4].Trim(), out _))
            .Where(l => l.Contains("get_") || l.Contains("set_"));
        Assert.Empty(accessorLines);
    }

    [Fact]
    public async Task Summary_ContainsDeadRatio()
    {
        var tool = new CSharpReferenceReportTool { Type = "QwenChatTemplate" };
        var result = await tool.ExecuteAsync(TestContext(), CancellationToken.None);

        Assert.Contains("dead ratio:", result);
    }

    [Fact]
    public async Task SaveDetail_WritesFullReportFile()
    {
        var file = Path.Combine(SelfBuildPaths.WorkspaceRoot, "reports", "reference_report_ShelfState.md");
        try
        {
            var tool = new CSharpReferenceReportTool { Type = "ShelfState", SaveDetail = true };
            var result = await tool.ExecuteAsync(TestContext(), CancellationToken.None);

            Assert.DoesNotContain("Ambiguous", result);
            Assert.Contains("full report:", result);
            Assert.True(File.Exists(file), "детальный отчёт не записан");
            var detail = File.ReadAllText(file);
            Assert.Contains("| refs | member | kind | first use | note |", detail);
            Assert.Contains("Activate", detail);
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
