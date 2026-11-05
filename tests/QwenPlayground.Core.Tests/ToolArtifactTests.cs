using System.IO;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;
using QwenPlayground.Core.Tools.Builtins;

namespace QwenPlayground.Core.Tests;

public sealed class ToolArtifactTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tool-artifacts-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Glob_OverPreviewLimit_AttachesEveryMatch()
    {
        Directory.CreateDirectory(_root);
        for (var i = 0; i < 250; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"file-{i:000}.txt"), "x");
        }
        var (context, message) = Context();
        var tool = new GlobTool { Pattern = "*.txt" };

        var preview = await tool.ExecuteAsync(context, CancellationToken.None);
        await tool.FinalizeAsync(context, message.Id, CancellationToken.None);

        var fullPath = Path.Combine(_root, "artifacts", $"msg_{message.Id}", "attachments", "glob.txt");
        var lines = File.ReadAllLines(fullPath);
        Assert.Equal(250, lines.Length);
        Assert.Contains("full 250 results attached", preview);
        Assert.Contains("file-249.txt", lines);
        Assert.Contains("<attachment path=", message.Content);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "artifacts", "pending")));
    }

    [Fact]
    public async Task Grep_OverPreviewLimit_AttachesEveryMatchWithoutMatchingItsOwnArtifact()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllLines(Path.Combine(_root, "many.txt"),
            Enumerable.Range(1, 150).Select(i => $"needle {i}"));
        var (context, message) = Context();
        var tool = new GrepTool { Pattern = "needle" };

        var preview = await tool.ExecuteAsync(context, CancellationToken.None);
        await tool.FinalizeAsync(context, message.Id, CancellationToken.None);

        var fullPath = Path.Combine(_root, "artifacts", $"msg_{message.Id}", "attachments", "grep.txt");
        var lines = File.ReadAllLines(fullPath);
        Assert.Equal(150, lines.Length);
        Assert.Contains("showing 100 of 150 matches", preview);
        Assert.Contains("needle 150", lines[^1]);
        Assert.DoesNotContain("artifacts/pending", string.Join('\n', lines));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "artifacts", "pending")));
    }

    private (ToolContext Context, ChatMessage Message) Context()
    {
        var log = new ChatLog();
        var message = ChatMessage.Tool("");
        log.Add(message);
        return (new ToolContext(_root, sessionDir: _root, conversation: log), message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
