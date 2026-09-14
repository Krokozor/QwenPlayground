using System.IO;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Desktop;

/// <summary>
/// Base class for desktop tools that auto-attach screenshots to the tool message.
/// Mirrors BrowserToolBase: the screenshot is attached via FinalizeAsync using
/// MessageMetaStore, so the model sees the image in the next render without extra tool calls.
/// Supports multiple screenshots (pipe-separated) for series tools.
/// </summary>
public abstract class DesktopToolBase : AgentTool
{
    protected string? _screenshotPath;

    protected void SetScreenshot(string path) => _screenshotPath = path;

    public override async Task FinalizeAsync(ToolContext context, int messageId, CancellationToken cancellationToken)
    {
        if (_screenshotPath is null) return;

        try
        {
            var sessionDir = context.SessionDir
                ?? Path.Combine(context.ProjectRoot, "sessions", "main");
            var store = new MessageMetaStore(sessionDir);

            var paths = _screenshotPath.Split('|');
            foreach (var p in paths)
            {
                if (File.Exists(p))
                    store.AddArtifact(messageId, p);
            }
        }
        catch
        {
            // Non-critical: if attachment fails, the tool text still has the path
        }
    }
}
