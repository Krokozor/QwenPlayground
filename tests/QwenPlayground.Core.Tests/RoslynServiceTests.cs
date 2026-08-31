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
}
