using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace QwenPlayground.App.Browser;

/// <summary>
/// Manages the WebView2 browser instance for the agent.
/// Static singleton — the WebView2 control lives in the visual tree (ChatView),
/// but the service is accessed from tools via this static class.
/// </summary>
public static class BrowserService
{
    private static WebView2? _webView;
    private static CoreWebView2? _core;
    private static CoreWebView2Environment? _env;
    private static CoreWebView2Controller? _controller;
    private static readonly object _lock = new();
    private static readonly string UserDataFolder =
        Path.Combine(Path.GetTempPath(), "QwenPlayground_WebView2");

    public static void Attach(WebView2 webView)
    {
        _webView = webView;
        _core = null;
        _controller = null;
        _env = null;
    }

    public static bool IsAttached => _webView is not null;

    public static bool HasCore
    {
        get { lock (_lock) return _core is not null; }
    }

    public static async Task<string> GetDiagnosticsAsync()
    {
        var webView = _webView;
        if (webView is null) return "No control";
        var w = webView.ActualWidth;
        var h = webView.ActualHeight;
        var hasCore = webView.CoreWebView2 is not null;
        return $"  Size: {w}x{h}, IsLoaded: {webView.IsLoaded}, CoreWebView2: {(hasCore ? "set" : "null")}";
    }

    private static async Task<CoreWebView2> GetCoreAsync()
    {
        lock (_lock)
        {
            if (_core is not null) return _core;
        }

        var webView = _webView;
        if (webView is null)
            throw new InvalidOperationException("Browser not attached. ChatView may not have loaded yet.");

        // Fast path: WPF control already initialized
        if (webView.CoreWebView2 is not null)
        {
            lock (_lock) { _core = webView.CoreWebView2; }
            return webView.CoreWebView2;
        }

        // Manual init via lower-level API (WPF control's internal init fails silently)
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Create environment
        if (_env is null)
        {
            Directory.CreateDirectory(UserDataFolder);
            _env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder, null);
        }

        // 2. Get the HWND (the control's visual host)
        var source = PresentationSource.FromVisual(webView) as HwndSource;
        if (source is null)
            throw new InvalidOperationException(
                $"No HwndSource for WebView2. IsLoaded={webView.IsLoaded}, Size={webView.ActualWidth}x{webView.ActualHeight}");
        var hwnd = source.Handle;

        // 3. Create controller bound to that HWND
        if (_controller is null)
        {
            _controller = await _env.CreateCoreWebView2ControllerAsync(hwnd);
        }

        // 4. Set bounds
        _controller.Bounds = new System.Drawing.Rectangle(0, 0,
            Math.Max(1, (int)webView.ActualWidth), Math.Max(1, (int)webView.ActualHeight));

        var core = _controller.CoreWebView2;
        lock (_lock) { _core = core; }

        return core;
    }

    // ─── Navigation ───────────────────────────────────────────

    public static async Task<string> NavigateAsync(string url)
    {
        var core = await GetCoreAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var tcs = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnCompleted;
            tcs.TrySetResult(e);
        }
        core.NavigationCompleted += OnCompleted;

        core.Navigate(url);
        var result = await Task.WhenAny(tcs.Task, Task.Delay(30_000));

        if (result == tcs.Task)
        {
            var navResult = await tcs.Task;
            sw.Stop();
            var status = navResult.IsSuccess ? "OK" : $"failed (HTTP {navResult.HttpStatusCode})";
            return $"Navigated to {url} ({sw.ElapsedMilliseconds}ms) — {status}";
        }
        else
        {
            core.NavigationCompleted -= OnCompleted;
            sw.Stop();
            return $"Navigated to {url} ({sw.ElapsedMilliseconds}ms) — timeout";
        }
    }

    // ─── Screenshot ───────────────────────────────────────────

    public static async Task<string> ScreenshotAsync()
    {
        var core = await GetCoreAsync();

        using var stream = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        stream.Position = 0;

        var path = Path.Combine(Path.GetTempPath(), $"browser_{DateTime.Now:HHmmss_fff}.png");
        using var fs = File.Create(path);
        stream.CopyTo(fs);

        return path;
    }

    // ─── Interaction ──────────────────────────────────────────

    public static async Task<string> ClickAsync(string selector)
    {
        var core = await GetCoreAsync();
        var js = BuildClickJs(selector);
        var result = await core.ExecuteScriptAsync(js);
        return Unquote(result) == "OK" ? $"Clicked {selector}" : $"Click failed: {Unquote(result)}";
    }

    public static async Task<string> TypeAsync(string selector, string text)
    {
        var core = await GetCoreAsync();
        var js = BuildTypeJs(selector, text);
        var result = await core.ExecuteScriptAsync(js);
        return Unquote(result) == "OK" ? $"Typed into {selector}" : $"Type failed: {Unquote(result)}";
    }

    public static async Task<string> SelectAsync(string selector, string value)
    {
        var core = await GetCoreAsync();
        var js = BuildSelectJs(selector, value);
        var result = await core.ExecuteScriptAsync(js);
        return Unquote(result) == "OK" ? $"Selected '{value}' in {selector}" : $"Select failed: {Unquote(result)}";
    }

    public static async Task<string> ScrollAsync(int deltaY)
    {
        var core = await GetCoreAsync();
        await core.ExecuteScriptAsync($"window.scrollBy(0, {deltaY}); 'OK'");
        return $"Scrolled by {deltaY}px";
    }

    public static async Task<string> HoverAsync(string selector)
    {
        var core = await GetCoreAsync();
        var js = BuildHoverJs(selector);
        var result = await core.ExecuteScriptAsync(js);
        return Unquote(result) == "OK" ? $"Hovered {selector}" : $"Hover failed: {Unquote(result)}";
    }

    public static async Task<string> WaitAsync(string selector, int timeoutMs)
    {
        var core = await GetCoreAsync();
        var js = BuildWaitJs(selector, timeoutMs);
        var result = await core.ExecuteScriptAsync(js);
        return Unquote(result) == "OK" ? $"Element {selector} appeared" : $"Timeout waiting for {selector}";
    }

    public static async Task<string> EvaluateAsync(string script)
    {
        var core = await GetCoreAsync();
        var result = await core.ExecuteScriptAsync(script);
        return Unquote(result);
    }

    public static async Task<string> ExtractAsync(string selector)
    {
        var core = await GetCoreAsync();
        var js = BuildExtractJs(selector);
        var result = await core.ExecuteScriptAsync(js);
        return Unquote(result);
    }

    public static async Task<string> KeyAsync(string key)
    {
        var core = await GetCoreAsync();
        var keyJs = key.ToLowerInvariant() switch
        {
            "enter" => "Enter",
            "tab" => "Tab",
            "escape" or "esc" => "Escape",
            "backspace" => "Backspace",
            "delete" or "del" => "Delete",
            "space" => " ",
            _ => key
        };
        var js = BuildKeyJs(keyJs);
        var result = await core.ExecuteScriptAsync(js);
        return Unquote(result) == "OK" ? $"Pressed {key}" : $"Key failed: {Unquote(result)}";
    }

    // ─── Cursor Overlay ───────────────────────────────────────

    public static async Task InjectCursorOverlayAsync()
    {
        var core = await GetCoreAsync();
        await core.ExecuteScriptAsync(CursorOverlayJs);
    }

    // ─── JS Builders ──────────────────────────────────────────

    private static string BuildClickJs(string selector) =>
        "(function() {" +
        "var el = document.querySelector(" + JsStr(selector) + ");" +
        "if (!el) return 'ERROR: not found: " + EscapeJs(selector) + "';" +
        "el.scrollIntoView({ block: 'center' });" +
        "el.style.outline = '3px solid #ff0'; el.style.outlineOffset = '2px';" +
        "setTimeout(function(){ el.style.outline=''; el.style.outlineOffset=''; }, 500);" +
        "el.click();" +
        "return 'OK';" +
        "})()";

    private static string BuildTypeJs(string selector, string text) =>
        "(function() {" +
        "var el = document.querySelector(" + JsStr(selector) + ");" +
        "if (!el) return 'ERROR: not found: " + EscapeJs(selector) + "';" +
        "el.scrollIntoView({ block: 'center' });" +
        "el.focus();" +
        "el.value = " + JsStr(text) + ";" +
        "el.dispatchEvent(new Event('input', { bubbles: true }));" +
        "el.dispatchEvent(new Event('change', { bubbles: true }));" +
        "return 'OK';" +
        "})()";

    private static string BuildSelectJs(string selector, string value) =>
        "(function() {" +
        "var el = document.querySelector(" + JsStr(selector) + ");" +
        "if (!el) return 'ERROR: not found: " + EscapeJs(selector) + "';" +
        "if (el.tagName !== 'SELECT') return 'ERROR: not a select';" +
        "el.value = " + JsStr(value) + ";" +
        "el.dispatchEvent(new Event('change', { bubbles: true }));" +
        "return 'OK';" +
        "})()";

    private static string BuildHoverJs(string selector) =>
        "(function() {" +
        "var el = document.querySelector(" + JsStr(selector) + ");" +
        "if (!el) return 'ERROR: not found: " + EscapeJs(selector) + "';" +
        "el.scrollIntoView({ block: 'center' });" +
        "el.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));" +
        "el.dispatchEvent(new MouseEvent('mouseenter', { bubbles: true }));" +
        "return 'OK';" +
        "})()";

    private static string BuildWaitJs(string selector, int timeoutMs) =>
        "(function() {" +
        "return new Promise(function(resolve) {" +
        "var el = document.querySelector(" + JsStr(selector) + ");" +
        "if (el) { resolve('OK'); return; }" +
        "var obs = new MutationObserver(function() {" +
        "var el = document.querySelector(" + JsStr(selector) + ");" +
        "if (el) { obs.disconnect(); resolve('OK'); }" +
        "});" +
        "obs.observe(document.body, { childList: true, subtree: true });" +
        "setTimeout(function(){ obs.disconnect(); resolve('TIMEOUT'); }, " + timeoutMs + ");" +
        "});" +
        "})()";

    private static string BuildExtractJs(string selector) =>
        "(function() {" +
        "var els = document.querySelectorAll(" + JsStr(selector) + ");" +
        "if (els.length === 0) return 'ERROR: no elements';" +
        "var parts = [];" +
        "els.forEach(function(el){ parts.push(el.innerText); });" +
        "return parts.join('\\n---\\n');" +
        "})()";

    private static string BuildKeyJs(string key) =>
        "(function() {" +
        "var el = document.activeElement || document.body;" +
        "el.dispatchEvent(new KeyboardEvent('keydown', { key: " + JsStr(key) + ", bubbles: true }));" +
        "el.dispatchEvent(new KeyboardEvent('keypress', { key: " + JsStr(key) + ", bubbles: true }));" +
        "el.dispatchEvent(new KeyboardEvent('keyup', { key: " + JsStr(key) + ", bubbles: true }));" +
        "return 'OK';" +
        "})()";

    private const string CursorOverlayJs =
        "(function() {" +
        "if (document.getElementById('__agent_cursor')) return 'OK';" +
        "var c = document.createElement('div');" +
        "c.id = '__agent_cursor';" +
        "c.style.cssText = 'position:fixed;width:20px;height:20px;border:3px solid #ff0;border-radius:50%;pointer-events:none;z-index:2147483647;transform:translate(-50%,-50%);box-shadow:0 0 6px #ff0,0 0 12px rgba(255,255,0,0.5);transition:left 0.1s,top 0.1s;';" +
        "document.body.appendChild(c);" +
        "document.addEventListener('mousemove', function(e){ c.style.left=e.clientX+'px'; c.style.top=e.clientY+'px'; }, true);" +
        "c.style.left = (window.innerWidth/2)+'px'; c.style.top = (window.innerHeight/2)+'px';" +
        "return 'OK';" +
        "})()";

    // ─── Helpers ──────────────────────────────────────────────

    private static string JsStr(string s) => "\"" + EscapeJs(s) + "\"";

    private static string EscapeJs(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("'", "\\'").Replace("\n", "\\n");

    private static string Unquote(string s)
    {
        if (s.StartsWith("\"") && s.EndsWith("\"") && s.Length >= 2)
        {
            try { return System.Text.Json.JsonDocument.Parse(s).RootElement.GetString() ?? s; }
            catch { return s[1..^1]; }
        }
        return s;
    }
}
