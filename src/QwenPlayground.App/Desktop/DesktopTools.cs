using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Desktop;

[Tool("desktop_screenshot",
    "Take a screenshot of the entire screen (all monitors) and attach it to this tool response " +
    "so you can SEE it in the next render. Use to observe the current desktop state. " +
    "Call remove_attachments when done looking to free context.",
    ToolGroup.Desktop)]
public sealed class DesktopScreenshotTool : DesktopToolBase
{
    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        var path = await DesktopService.ScreenshotAsync();
        SetScreenshot(path);
        return "Desktop screenshot captured.";
    }
}

[Tool("desktop_grid_screenshot",
    "Take a screenshot with a COORDINATE GRID overlay (blue lines every 200px + numeric labels). " +
    "Use for PRECISE TARGETING: read the coordinates of your target from the grid, " +
    "then use desktop_move to aim and desktop_click to click. " +
    "Workflow: grid_screenshot → read target coords → desktop_move (aim) → desktop_click (fire).",
    ToolGroup.Desktop)]
public sealed class DesktopGridScreenshotTool : DesktopToolBase
{
    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        var path = await DesktopService.GridScreenshotAsync();
        SetScreenshot(path);
        return "Grid screenshot captured. Read coordinates from the blue grid lines (every 200px).";
    }
}

[Tool("desktop_click",
    "Click at screen coordinates (x, y). Moves the cursor there first, then clicks. " +
    "Button: 'left' (default), 'right' (context menu), 'middle'. " +
    "Clicks: 1 (default), 2 (double-click), 3 (triple-click). " +
    "Returns: text result + screenshot with cursor overlay at the click point. " +
    "RECOMMENDED: use desktop_move first to verify the target before clicking.",
    ToolGroup.Desktop)]
public sealed class DesktopClickTool : DesktopToolBase
{
    [ToolParameter("X coordinate on screen (pixels from left edge of virtual screen)", Required = true)]
    public int X { get; set; }
    [ToolParameter("Y coordinate on screen (pixels from top edge of virtual screen)", Required = true)]
    public int Y { get; set; }
    [ToolParameter("Mouse button: 'left' (default), 'right', or 'middle'", Required = false)]
    public string Button { get; set; } = "left";
    [ToolParameter("Number of clicks: 1 (default), 2 (double-click), 3 (triple-click)", Required = false)]
    public int Clicks { get; set; } = 1;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        var result = await DesktopService.ClickAsync(X, Y, Button, Clicks);
        await Task.Delay(300, ct);
        var screenshotPath = await DesktopService.ScreenshotAsync(X, Y);
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("desktop_move",
    "Move the cursor to screen coordinates (x, y) and report what window is at that point. " +
    "USE THIS TO AIM BEFORE CLICKING: move → verify → click. " +
    "Returns: window info (title, class, bounds) at the point + screenshot with cursor overlay. " +
    "This is your 'hover to inspect' — like moving a real mouse without clicking.",
    ToolGroup.Desktop)]
public sealed class DesktopMoveTool : DesktopToolBase
{
    [ToolParameter("X coordinate on screen", Required = true)]
    public int X { get; set; }
    [ToolParameter("Y coordinate on screen", Required = true)]
    public int Y { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        DesktopService.MoveCursor(X, Y);
        await Task.Delay(100, ct);
        var info = DesktopService.GetInfoAtPoint(X, Y);
        var screenshotPath = await DesktopService.ScreenshotAsync(X, Y);
        SetScreenshot(screenshotPath);
        return info;
    }
}

[Tool("desktop_type",
    "Type text at the current cursor position (wherever the keyboard focus is). " +
    "Uses Unicode input — layout-independent, works with any keyboard layout. " +
    "NOTE: does NOT click or focus anything — the text goes to whatever has keyboard focus. " +
    "Use desktop_click first to focus the target input field. " +
    "Returns: text result + screenshot.",
    ToolGroup.Desktop)]
public sealed class DesktopTypeTool : DesktopToolBase
{
    [ToolParameter("Text to type", Required = true)]
    public string Text { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        var result = await DesktopService.TypeTextAsync(Text);
        await Task.Delay(200, ct);
        var pos = DesktopService.GetCursorPosition();
        var screenshotPath = await DesktopService.ScreenshotAsync(pos.X, pos.Y);
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("desktop_key",
    "Press a keyboard key or key combination. " +
    "Keys: enter, tab, escape, backspace, delete, space, arrowup/down/left/right, home, end, " +
    "pageup, pagedown, insert, f1-f12, or a single character (a-z, 0-9). " +
    "Modifiers: 'ctrl', 'shift', 'alt', 'win' — combine with '+' (e.g. 'ctrl+shift'). " +
    "Examples: key='enter', key='c' modifiers='ctrl', key='s' modifiers='ctrl', key='a' modifiers='ctrl'. " +
    "Returns: text result + screenshot.",
    ToolGroup.Desktop)]
public sealed class DesktopKeyTool : DesktopToolBase
{
    [ToolParameter("Key to press: enter, tab, escape, backspace, delete, space, arrowup, arrowdown, f1-f12, or a single char", Required = true)]
    public string Key { get; set; } = string.Empty;
    [ToolParameter("Modifier keys: combination of ctrl, shift, alt, win (e.g. 'ctrl', 'ctrl+shift')", Required = false)]
    public string Modifiers { get; set; } = "";

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        var result = await DesktopService.PressKeyAsync(Key, Modifiers);
        await Task.Delay(200, ct);
        var pos = DesktopService.GetCursorPosition();
        var screenshotPath = await DesktopService.ScreenshotAsync(pos.X, pos.Y);
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("desktop_scroll",
    "Scroll at screen coordinates (x, y). Moves the cursor there first, then scrolls. " +
    "DeltaY: positive = scroll down, negative = scroll up (in pixels). " +
    "Returns: text result + screenshot with cursor overlay.",
    ToolGroup.Desktop)]
public sealed class DesktopScrollTool : DesktopToolBase
{
    [ToolParameter("X coordinate on screen", Required = true)]
    public int X { get; set; }
    [ToolParameter("Y coordinate on screen", Required = true)]
    public int Y { get; set; }
    [ToolParameter("Pixels to scroll (positive=down, negative=up). E.g. 300 or -200", Required = true)]
    public int DeltaY { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        var result = await DesktopService.ScrollAsync(X, Y, DeltaY);
        await Task.Delay(300, ct);
        var screenshotPath = await DesktopService.ScreenshotAsync(X, Y);
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("desktop_drag",
    "Drag from (x1, y1) to (x2, y2) with the given mouse button. " +
    "Moves cursor to start point, presses button, moves to end point in steps, releases. " +
    "Button: 'left' (default), 'right', 'middle'. " +
    "Returns: text result + screenshot with cursor overlay at the end point.",
    ToolGroup.Desktop)]
public sealed class DesktopDragTool : DesktopToolBase
{
    [ToolParameter("Start X coordinate", Required = true)]
    public int X1 { get; set; }
    [ToolParameter("Start Y coordinate", Required = true)]
    public int Y1 { get; set; }
    [ToolParameter("End X coordinate", Required = true)]
    public int X2 { get; set; }
    [ToolParameter("End Y coordinate", Required = true)]
    public int Y2 { get; set; }
    [ToolParameter("Mouse button: 'left' (default), 'right', or 'middle'", Required = false)]
    public string Button { get; set; } = "left";

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        var result = await DesktopService.DragAsync(X1, Y1, X2, Y2, Button);
        await Task.Delay(300, ct);
        var screenshotPath = await DesktopService.ScreenshotAsync(X2, Y2);
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("list_windows",
    "List all visible top-level windows with title, class name, bounds, and hwnd. " +
    "The foreground window is marked with ▶. " +
    "Returns: text list (no screenshot). Use focus_window to bring a window to front.",
    ToolGroup.Desktop)]
public sealed class ListWindowsTool : AgentTool
{
    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        return Task.FromResult(DesktopService.ListWindows());
    }
}

[Tool("focus_window",
    "Bring a window to the foreground by title (partial match, case-insensitive). " +
    "Use list_windows first to see available window titles. " +
    "Returns: text result + screenshot of the desktop after focusing.",
    ToolGroup.Desktop)]
public sealed class FocusWindowTool : DesktopToolBase
{
    [ToolParameter("Window title to search for (partial match, case-insensitive)", Required = true)]
    public string Title { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        var result = await DesktopService.FocusWindowAsync(Title);
        await Task.Delay(300, ct);
        var screenshotPath = await DesktopService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}
