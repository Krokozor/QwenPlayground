using System.IO;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Browser;

/// <summary>
/// Base class for browser tools that auto-attach screenshots to the tool message.
/// The screenshot is attached via FinalizeAsync using MessageMetaStore,
/// so the model sees the image in the next render without extra tool calls.
/// </summary>
public abstract class BrowserToolBase : AgentTool
{
    private string? _screenshotPath;

    protected void SetScreenshot(string path) => _screenshotPath = path;

    public override async Task FinalizeAsync(ToolContext context, int messageId, CancellationToken cancellationToken)
    {
        if (_screenshotPath is null || !File.Exists(_screenshotPath))
            return;

        try
        {
            var sessionDir = context.SessionDir
                ?? Path.Combine(context.ProjectRoot, "sessions", "main");
            var store = new MessageMetaStore(sessionDir);
            store.AddArtifact(messageId, _screenshotPath);
        }
        catch
        {
            // Non-critical: if attachment fails, the tool text still has the path
        }
    }
}
