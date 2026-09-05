using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Browser;

[Tool("browser_navigate", "Navigate the agent's browser. Action='navigate' (default): go to Url, waits for page load (up to 30s). " +
                          "Action='back'/'forward': move through page history (no Url needed). Action='reload': refresh the current page. " +
                          "ALWAYS use Action='navigate' with a URL first before interacting with a site. " +
                          "Returns: text result + screenshot of the resulting page. " +
                          "If the internet is down, you'll get a timeout error after 30s.", ToolGroup.Browser)]
public sealed class BrowserNavigateTool : BrowserToolBase
{
    [ToolParameter("Action: 'navigate' (default, go to Url), 'back', 'forward', or 'reload'", Required = false)]
    public string Action { get; set; } = "navigate";

    [ToolParameter("The URL to navigate to (required for Action='navigate', must start with http:// or https://)", Required = false)]
    public string Url { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached)
            return "Error: browser not available. ChatView may not have loaded yet.";

        var result = await BrowserService.NavigateActionAsync(Action, Url, ct);
        await BrowserService.InjectCursorOverlayAsync();
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_click", "Click an element by CSS selector. The element is highlighted (magenta outline) before clicking. " +
                       "If the click triggers navigation, waits up to 2s for the new page. " +
                       "Use for: links, buttons, form elements with known selectors. " +
                       "Returns: text result + screenshot after the action. " +
                       "If selector not found, returns 'Click failed: ERROR: not found: ...'", ToolGroup.Browser)]
public sealed class BrowserClickTool : BrowserToolBase
{
    [ToolParameter("CSS selector (e.g. '#submit', 'a[href=\"/newest\"]', 'button.login', '.nav a:nth(2)')", Required = true)]
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

[Tool("browser_click_at", "Click at specific viewport coordinates (x, y) in the 1280x800 virtual viewport. " +
                          "Use when: the target is a canvas, SVG, or element without a reliable CSS selector. " +
                          "RECOMMENDED: use browser_cursor_move first to verify the target before clicking. " +
                          "Set Trusted=true for a CDP click (isTrusted=true) if the site ignores normal clicks (anti-bot). " +
                          "If the click triggers navigation, waits up to 2s. " +
                          "Returns: text result + screenshot.", ToolGroup.Browser)]
public sealed class BrowserClickAtTool : BrowserToolBase
{
    [ToolParameter("X coordinate in viewport (0-1280)", Required = true)]
    public int X { get; set; }
    [ToolParameter("Y coordinate in viewport (0-800)", Required = true)]
    public int Y { get; set; }
    [ToolParameter("Use trusted CDP click (isTrusted=true) — when the site ignores normal clicks", Required = false)]
    public bool Trusted { get; set; } = false;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.ClickAtAsync(X, Y, Trusted);
        await Task.Delay(300);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_cursor_move", "Move the cursor to (x, y) and report what element is at that point. " +
                             "USE THIS TO AIM BEFORE CLICKING: move → verify → click_at. " +
                             "Returns: element info (tag, id, class, href, text snippet) + screenshot with cursor visible. " +
                             "This is your 'hover to inspect' — like moving a real mouse without clicking.", ToolGroup.Browser)]
public sealed class BrowserCursorMoveTool : BrowserToolBase
{
    [ToolParameter("X coordinate in viewport (0-1280)", Required = true)]
    public int X { get; set; }
    [ToolParameter("Y coordinate in viewport (0-800)", Required = true)]
    public int Y { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.CursorMoveAsync(X, Y);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_type", "Type text into an input/textarea. Mode='set' (default): sets value at once, fast. " +
                      "Mode='type': types character-by-character with key events — use for React/controlled inputs " +
                      "and debounced live-search where setting the value at once doesn't register. " +
                      "Triggers autocomplete on sites like Google. " +
                      "NOTE: does NOT submit forms — use browser_key(enter, selector) or click submit button. " +
                      "Returns: text result + screenshot (you'll see autocomplete if it appeared).", ToolGroup.Browser)]
public sealed class BrowserTypeTool : BrowserToolBase
{
    [ToolParameter("CSS selector of the input/textarea", Required = true)]
    public string Selector { get; set; } = string.Empty;
    [ToolParameter("Text to type into the field", Required = true)]
    public string Text { get; set; } = string.Empty;
    [ToolParameter("Mode: 'set' (default, value at once) or 'type' (character-by-character with key events)", Required = false)]
    public string Mode { get; set; } = "set";

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.TypeAsync(Selector, Text, Mode);
        await Task.Delay(200);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_key", "Press a keyboard key. If selector is provided, focuses that element first. " +
                     "Keys: enter, tab, escape, backspace, delete, space, arrowup/down/left/right, home, end, or single char. " +
                     "Set Trusted=true to dispatch via CDP (isTrusted=true) — use when the site ignores synthetic events " +
                     "(e.g. Google form submit checks isTrusted). " +
                     "Returns: text result + screenshot.", ToolGroup.Browser)]
public sealed class BrowserKeyTool : BrowserToolBase
{
    [ToolParameter("Key: enter, tab, escape, backspace, delete, space, arrowup, arrowdown, or a character", Required = true)]
    public string Key { get; set; } = string.Empty;
    [ToolParameter("Optional CSS selector to focus before pressing (e.g. 'textarea[name=\"q\"]')")]
    public string? Selector { get; set; }
    [ToolParameter("Dispatch via CDP (isTrusted=true) — when the site ignores synthetic key events", Required = false)]
    public bool Trusted { get; set; } = false;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.KeyAsync(Key, Selector, Trusted);
        await Task.Delay(200);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_select", "Select a value in a <select> dropdown. Fires change event. " +
                        "Returns: text result + screenshot.", ToolGroup.Browser)]
public sealed class BrowserSelectTool : BrowserToolBase
{
    [ToolParameter("CSS selector of the <select> element", Required = true)]
    public string Selector { get; set; } = string.Empty;
    [ToolParameter("The option value to select (must match <option value=\"...\">)", Required = true)]
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

[Tool("browser_scroll", "Scroll the page vertically. Positive = down, negative = up. " +
                        "Returns: scroll position (current/total) + screenshot. " +
                        "Use browser_screenshot_full instead if you need to see the whole page at once.", ToolGroup.Browser)]
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

[Tool("browser_hover", "Hover over an element (triggers :hover styles, dropdowns, tooltips). " +
                       "Set Trusted=true for a CDP mouse move (isTrusted=true) — use when the normal hover " +
                       "doesn't open the menu (real :hover dropdowns on React etc.). " +
                        "Returns: text result + screenshot (you'll see the tooltip/dropdown if it appeared).", ToolGroup.Browser)]
public sealed class BrowserHoverTool : BrowserToolBase
{
    [ToolParameter("CSS selector of the element to hover", Required = true)]
    public string Selector { get; set; } = string.Empty;
    [ToolParameter("Use trusted CDP mouse move (isTrusted=true) — when normal hover doesn't open the menu", Required = false)]
    public bool Trusted { get; set; } = false;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.HoverAsync(Selector, Trusted);
        await Task.Delay(300);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_wait", "Wait for an element to appear or disappear (polls the page every 200ms). " +
                      "Mode='appear' (default): wait until the element exists — use after actions that trigger " +
                      "async loading (AJAX, SPA routing). Mode='absent': wait until the element is gone " +
                      "(spinner finished, modal closed). " +
                      "Returns: result + screenshot of current state.", ToolGroup.Browser)]
public sealed class BrowserWaitTool : BrowserToolBase
{
    [ToolParameter("CSS selector to wait for", Required = true)]
    public string Selector { get; set; } = string.Empty;
    [ToolParameter("Timeout in ms (default 10000)", Required = false)]
    public int TimeoutMs { get; set; } = 10_000;
    [ToolParameter("Mode: 'appear' (default, wait until element exists) or 'absent' (wait until element is gone)", Required = false)]
    public string Mode { get; set; } = "appear";

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var result = await BrowserService.WaitAsync(Selector, TimeoutMs, Mode);
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return result;
    }
}

[Tool("browser_screenshot", "Take a screenshot of the current viewport (1280x800). " +
                            "Use to see the current page state without performing an action. " +
                            "Returns: ONLY a screenshot (no text). " +
                            "For the FULL page (all scroll content), use browser_screenshot_full.", ToolGroup.Browser)]
public sealed class BrowserScreenshotTool : BrowserToolBase
{
    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var screenshotPath = await BrowserService.ScreenshotAsync();
        SetScreenshot(screenshotPath);
        return "Screenshot captured (viewport 1280x800).";
    }
}

[Tool("browser_screenshot_full", "Screenshot the ENTIRE page by temporarily expanding the viewport to full scroll height. " +
                                 "Use when: you need to see content below the fold without scrolling. " +
                                 "The viewport is restored to 1280x800 after capture. " +
                                 "Max height: 16000px. For very long pages the image will be large. " +
                                 "Returns: text + full-page screenshot.", ToolGroup.Browser)]
public sealed class BrowserScreenshotFullTool : BrowserToolBase
{
    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var screenshotPath = await BrowserService.ScreenshotFullPageAsync();
        SetScreenshot(screenshotPath);
        return "Full-page screenshot captured.";
    }
}

[Tool("browser_screenshot_series", "Take multiple screenshots at regular intervals to observe page DYNAMICS. " +
                                   "Use for: watching animations, loading spinners, transitions, video, progress bars. " +
                                   "Example: count=5, interval=1000 → 5 frames over 5 seconds. " +
                                   "Returns: text + ALL screenshots attached (you'll see the sequence).", ToolGroup.Browser)]
public sealed class BrowserScreenshotSeriesTool : BrowserToolBase
{
    [ToolParameter("Number of frames to capture (1-20)", Required = true)]
    public int Count { get; set; } = 5;
    [ToolParameter("Interval between frames in ms (default 1000)", Required = false)]
    public int IntervalMs { get; set; } = 1000;

    private List<string> _paths = new();

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        Count = Math.Clamp(Count, 1, 20);
        IntervalMs = Math.Clamp(IntervalMs, 100, 10000);
        _paths = await BrowserService.ScreenshotSeriesAsync(Count, IntervalMs);
        // Attach all screenshots (pipe-separated in _screenshotPath)
        _screenshotPath = string.Join("|", _paths);
        return $"Captured {_paths.Count} frames at {IntervalMs}ms interval.";
    }
}

[Tool("browser_extract", "Read text content from elements matching a CSS selector — line-based, like file_read. " +
                         "Returns lines Offset..Offset+Limit-1 of the elements' innerText (default: first 100 lines) " +
                         "+ how many lines remain; pass Offset to continue. NO screenshot. " +
                         "Use for: reading tables, lists, prices, article text without visual inspection. " +
                         "To LOCATE text on the page (where to scroll) use browser_find instead. " +
                         "Text inside <iframe>s is not visible — use browser_evaluate (iframe.contentDocument) for same-origin frames. " +
                         "Use for: reading tables, lists, prices, article text without visual inspection.", ToolGroup.Browser)]
public sealed class BrowserExtractTool : AgentTool
{
    [ToolParameter("CSS selector (e.g. 'table', '.price', 'h1, h2', 'article p')", Required = true)]
    public string Selector { get; set; } = string.Empty;
    [ToolParameter("First line to read, 0-based (default 0)", Required = false)]
    public int Offset { get; set; }
    [ToolParameter("Max lines to read (default 100, max 500)", Required = false)]
    public int Limit { get; set; } = 100;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        return await BrowserService.ExtractAsync(Selector, Math.Max(0, Offset), Math.Clamp(Limit, 1, 500));
    }
}

[Tool("browser_find", "Search for text on the page and tell you WHERE each match is, so you can scroll to it " +
                      "instead of scrolling blindly. Returns total count + for each match: approximate scroll " +
                      "position (px from top of page & % of page height), visibility, the element, and a context " +
                      "snippet. Case-insensitive by default. Max 50 matches (more common words are capped). " +
                      "By default NO screenshot (text only, cheap). Set MatchIndex (0-based) to scroll to and " +
                      "highlight that specific match — then a screenshot is attached showing it in the viewport. " +
                      "Text in <script>/<style>/<noscript>/<template> (raw code) is excluded; CSS-hidden " +
                      "content is still listed but marked [hidden]. Text inside <iframe>s is not searched — " +
                      "use browser_evaluate (iframe.contentDocument) for same-origin frames. " +
                      "To READ full element content (not just locate it) use browser_extract instead.", ToolGroup.Browser)]
public sealed class BrowserFindTool : BrowserToolBase
{
    [ToolParameter("Text to search for on the page", Required = true)]
    public string Query { get; set; } = string.Empty;

    [ToolParameter("Case-sensitive search (default false)", Required = false)]
    public bool CaseSensitive { get; set; } = false;

    [ToolParameter("0-based index of a match to scroll to and highlight (returns a screenshot). Omit for a text-only list.", Required = false)]
    public int MatchIndex { get; set; } = -1;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        var (text, jumped) = await BrowserService.FindAsync(Query, CaseSensitive, MatchIndex);
        if (jumped)
        {
            await Task.Delay(300);
            var screenshotPath = await BrowserService.ScreenshotAsync();
            SetScreenshot(screenshotPath);
        }
        return text;
    }
}

[Tool("browser_evaluate", "Execute arbitrary JavaScript on the page and return the result. " +
                          "POWER TOOL — use for anything not covered by other tools. " +
                          "Examples: form.submit(), getComputedStyle(el), localStorage.getItem('token'). " +
                          "Returns: JS result as text + screenshot.", ToolGroup.Browser)]
public sealed class BrowserEvaluateTool : BrowserToolBase
{
    [ToolParameter("JavaScript expression to evaluate (must return a value)", Required = true)]
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

[Tool("browser_console", "Read recent console messages (log, warn, error, info) from the page. " +
                         "Use for DEBUGGING: if a page is broken, behaving unexpectedly, or not loading, " +
                         "check the console for JS errors. " +
                         "Returns: JSON array of {level, msg, t} objects (last 100). NO screenshot. " +
                         "Note: captures messages from page load onwards (interceptor pre-injected).", ToolGroup.Browser)]
public sealed class BrowserConsoleTool : AgentTool
{
    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        return await BrowserService.GetConsoleAsync();
    }
}

[Tool("browser_network", "Show recent HTTP requests made by the page (method, status, type, URL). " +
                         "Use for: debugging failed loads, seeing what API calls a page makes, " +
                         "detecting 404/500 errors, understanding page structure. " +
                         "Returns: list of recent requests (last 30). NO screenshot. " +
                         "Network monitoring is enabled automatically after first navigation.", ToolGroup.Browser)]
public sealed class BrowserNetworkTool : AgentTool
{
    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        if (!BrowserService.IsAttached) return "Error: browser not available.";
        await BrowserService.EnsureResumedAsync();
        BrowserService.TouchActivity();
        return BrowserService.GetNetworkLog();
    }
}

