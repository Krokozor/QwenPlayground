using System.Text.RegularExpressions;
using QwenPlayground.Core.Roslyn;

namespace QwenPlayground.Core.Tests;

public sealed class RoslynServiceTests
{
    [Fact]
    public async Task Diagnostics_ReturnsCollection()
    {
        var tool = new CSharpDiagnosticsTool();
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.False(result.StartsWith("Error"), result);
    }

    [Fact]
    public async Task Symbol_FindsKnownType()
    {
        var tool = new CSharpSymbolTool { Name = "QwenChatTemplate" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.Contains("QwenChatTemplate", result);
        Assert.Contains("QwenChatTemplate.cs", result.Replace('\\', '/'));
    }

    [Fact]
    public async Task Outline_ListsTypesAndMembers()
    {
        var tool = new CSharpOutlineTool { Path = @"src\QwenPlayground.Core\Chat\ChatMessage.cs" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.Contains("class ChatMessage", result);
        Assert.Contains("property", result);
    }

    [Fact]
    public async Task ClassMap_FindsTypeAndMembers()
    {
        var tool = new CSharpClassMapTool { Name = "QwenChatTemplate" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.Contains("class QwenChatTemplate", result);
        Assert.Contains("QwenChatTemplate.cs", result.Replace('\\', '/'));
        Assert.Contains("method ", result);
    }

    [Fact]
    public async Task References_FindsUsagesOfKnownType()
    {
        var tool = new CSharpReferencesTool { Name = "QwenChatTemplate" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.Contains("QwenChatTemplate", result);
        Assert.Contains(".cs", result);
    }

    [Fact]
    public async Task Callers_FindsCallersOfKnownMethod()
    {
        var tool = new CSharpCallersTool { Name = "GetSolutionAsync" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.Contains("GetSolutionAsync", result);
    }

    [Fact]
    public async Task Definition_FindsDeclarationOfKnownType()
    {
        const string relativePath = "src/QwenPlayground.Core/Templates/QwenChatTemplate.cs";
        var source = File.ReadAllText(System.IO.Path.Combine(QwenPlayground.Core.SelfBuild.SelfBuildPaths.WorkspaceRoot, relativePath));
        var line = source.Split('\n').Select((text, i) => (text, i)).First(t => t.text.Contains("class QwenChatTemplate")).i + 1;

        var tool = new CSharpDefinitionTool { Path = relativePath, Line = line, Name = "QwenChatTemplate" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.False(result.StartsWith("Error"), result);
        Assert.Contains("QwenChatTemplate", result);
    }

    // ── Информативность координат: диапазон строк + комментарий над декларацией.
    // Тул не гадает: у многострочного элемента видно, где он кончается, и что он делает.

    // Диапазон строк в любом из форматов: «path:39-69» (symbol/definition/references),
    // «class X :32-212» (outline/class_map), колонка «declaration» отчёта.
    private static bool HasLineRange(string text) =>
        Regex.IsMatch(text, @":\d+-\d+");

    [Fact]
    public async Task Symbol_MultiLineDeclaration_ShowsRangeAndDoc()
    {
        var tool = new CSharpSymbolTool { Name = "ApplyChangesAsync" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.Contains("RoslynService.cs", result);
        Assert.True(HasLineRange(result), $"ожидается диапазон строк, получено: {result}");
        Assert.Contains("//", result); // описание из XML-doc над методом
    }

    [Fact]
    public async Task Symbol_SameDeclarationFromManyProjects_ReportedOnce()
    {
        // Символ находится по разу из каждого проекта, ссылающегося на его проект:
        // без дедупа по месту декларации выдача из семи одинаковых строк.
        var tool = new CSharpSymbolTool { Name = "GetSolutionAsync" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }

    [Fact]
    public async Task Definition_ShowsRangeAndDoc()
    {
        const string relativePath = "src/QwenPlayground.Core/Roslyn/LocationFormatter.cs";
        var full = System.IO.Path.Combine(QwenPlayground.Core.SelfBuild.SelfBuildPaths.WorkspaceRoot, relativePath);
        var line = File.ReadAllText(full).Split('\n')
            .Select((text, i) => (text, i))
            .First(t => t.text.Contains("public static string? Usage(")).i + 1;

        var tool = new CSharpDefinitionTool { Path = relativePath, Line = line, Name = "Usage" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.True(HasLineRange(result), $"ожидается диапазон строк, получено: {result}");
        Assert.Contains("//", result);
    }

    [Fact]
    public async Task Outline_ShowsRangesAndDocs()
    {
        var tool = new CSharpOutlineTool { Path = @"src\QwenPlayground.Core\Roslyn\LocationFormatter.cs" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.True(HasLineRange(result), $"ожидаются диапазоны строк, получено: {result}");
        Assert.Contains("//", result);
    }

    [Fact]
    public async Task ClassMap_SameTypeFromManyProjects_PrintedOnce()
    {
        var tool = new CSharpClassMapTool { Name = "ShelfState" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.Equal(1, Occurrences(result, "class ShelfState "));
        Assert.True(HasLineRange(result), $"ожидается диапазон строк, получено: {result}");
    }

    [Fact]
    public async Task References_GroupedByDeclarationWithRange()
    {
        var tool = new CSharpReferencesTool { Name = "ApplyChangesAsync" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.Contains("reference(s) to 'ApplyChangesAsync'", result);
        Assert.True(HasLineRange(result), $"ожидается диапазон строк декларации, получено: {result}");
        // Одна группа на декларацию, а не на каждый ссылающийся проект.
        Assert.Equal(1, Occurrences(result, "method "));
    }

    [Fact]
    public async Task Callers_GroupedByDeclaration()
    {
        var tool = new CSharpCallersTool { Name = "GetSolutionAsync" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.Contains("caller(s) of 'GetSolutionAsync' in 1 declaration(s)", result);
        Assert.True(HasLineRange(result), $"ожидается диапазон строк декларации, получено: {result}");
    }

    [Fact]
    public async Task ReferenceReport_RowsCarryDeclarationAndDoc()
    {
        var tool = new CSharpReferenceReportTool { Type = "ShelfState" };
        var result = await tool.ExecuteAsync(new QwenPlayground.Core.Tools.ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.Contains("declaration", result);
        Assert.Contains("//", result); // описание из комментария над членом
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
