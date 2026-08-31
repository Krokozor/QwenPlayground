using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Browser;

[Tool("browser_navigate", "Navigate the built-in browser to a URL. Waits for page load. " +
                          "Returns a screenshot of the loaded page so you can see what's there. " +
                          "Use this as the first step before interacting with a web page.")]
public sealed class BrowserNavigateTool : BrowserToolBase
{
    [ToolParameter("The URL to navigate to", Required = true)]
    public string Url { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached)
            return "Error: browser not available. ChatView may not have loaded yet.";

        var result = await BrowserService.NavigateAsync(Url);
        await BrowserService.InjectCursorOverlayAsync();
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_click", "Click an element on the current page by CSS selector. " +
                       "The element is highlighted briefly before clicking. Returns a screenshot after the click.")]
public sealed class BrowserClickTool : BrowserToolBase
{
    [ToolParameter("CSS selector of the element to click (e.g. '#submit', 'button.login', '.nav a:nth(2)')", Required = true)]
    public string Selector { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.ClickAsync(Selector);
        await Task.Delay(300);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_type", "Type text into an input/textarea element on the current page. " +
                      "Sets the value and fires input+change events. Returns a screenshot.")]
public sealed class BrowserTypeTool : BrowserToolBase
{
    [ToolParameter("CSS selector of the input element", Required = true)]
    public string Selector { get; set; } = string.Empty;
    [ToolParameter("Text to type", Required = true)]
    public string Text { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.TypeAsync(Selector, Text);
        await Task.Delay(200);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_select", "Select a value in a <select> dropdown element. Returns a screenshot.")]
public sealed class BrowserSelectTool : BrowserToolBase
{
    [ToolParameter("CSS selector of the select element", Required = true)]
    public string Selector { get; set; } = string.Empty;
    [ToolParameter("The value to select (must match an <option> value)", Required = true)]
    public string Value { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.SelectAsync(Selector, Value);
        await Task.Delay(200);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_scroll", "Scroll the page by a pixel amount (positive = down, negative = up). Returns a screenshot.")]
public sealed class BrowserScrollTool : BrowserToolBase
{
    [ToolParameter("Pixels to scroll (positive=down, negative=up). E.g. 500 or -300", Required = true)]
    public int DeltaY { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.ScrollAsync(DeltaY);
        await Task.Delay(300);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_hover", "Hover the mouse over an element (triggers hover effects, dropdowns, tooltips). Returns a screenshot.")]
public sealed class BrowserHoverTool : BrowserToolBase
{
    [ToolParameter("CSS selector of the element to hover", Required = true)]
    public string Selector { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.HoverAsync(Selector);
        await Task.Delay(300);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_wait", "Wait for an element to appear on the page (polls via MutationObserver). " +
                      "Use after actions that trigger async content loading. Returns a screenshot.")]
public sealed class BrowserWaitTool : BrowserToolBase
{
    [ToolParameter("CSS selector to wait for", Required = true)]
    public string Selector { get; set; } = string.Empty;
    [ToolParameter("Timeout in milliseconds (default 10000)")]
    public int TimeoutMs { get; set; } = 10_000;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.WaitAsync(Selector, TimeoutMs);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_screenshot", "Take a screenshot of the current browser page. " +
                            "Use to see the current state without performing an action.")]
public sealed class BrowserScreenshotTool : BrowserToolBase
{
    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return "Screenshot captured.";
    }
}

[Tool("browser_extract", "Extract text content from elements matching a CSS selector. " +
                         "Returns the innerText of all matching elements. No screenshot.")]
public sealed class BrowserExtractTool : AgentTool
{
    [ToolParameter("CSS selector (e.g. 'table', '.price', 'h1, h2')", Required = true)]
    public string Selector { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        return await BrowserService.ExtractAsync(Selector);
    }
}

[Tool("browser_evaluate", "Execute arbitrary JavaScript on the current page and return the result. " +
                          "Power tool — use for complex interactions not covered by other browser tools. Returns a screenshot.")]
public sealed class BrowserEvaluateTool : BrowserToolBase
{
    [ToolParameter("JavaScript expression to evaluate", Required = true)]
    public string Script { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.EvaluateAsync(Script);
        await Task.Delay(200);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return $"Result: {result}";
    }
}

[Tool("browser_key", "Press a keyboard key (Enter, Tab, Escape, Backspace, Delete, Space, or any single character). Returns a screenshot.")]
public sealed class BrowserKeyTool : BrowserToolBase
{
    [ToolParameter("Key to press: enter, tab, escape, backspace, delete, space, or a character", Required = true)]
    public string Key { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.KeyAsync(Key);
        await Task.Delay(200);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}
