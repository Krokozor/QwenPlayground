using QwenPlayground.Core.Tools;
using QwenPlayground.Core.Tools.Builtins;

namespace QwenPlayground.Core.Tests;

public sealed class SanityCheckToolTests
{
    [Fact]
    public async Task EmptyText_ReturnsError_WithoutSideEffects()
    {
        var tool = new SanityCheckTool();

        var result = await tool.ExecuteAsync(new ToolContext(Path.GetTempPath()), CancellationToken.None);

        Assert.Contains("пусто", result);
    }
}
