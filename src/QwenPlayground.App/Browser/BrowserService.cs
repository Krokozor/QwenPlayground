using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using QwenPlayground.Core.SelfBuild;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace QwenPlayground.App.Browser;

/// <summary>
/// Manages the WebView2 browser instance for the agent.
/// 
/// ARCHITECTURE NOTE (why this is the way it is — read before modifying):
/// 
/// 1. The WPF WebView2 control's internal init (CoreWebView2InitializationCompleted)
///    FAILS SILENTLY in this app — event never fires, CoreWebView2 stays null.
///    Workaround: we create CoreWebView2Environment + CoreWebView2Controller MANUALLY.
/// 
/// 2. Controller MUST have IsVisible=true for WebView2 to render and navigate.
///    If IsVisible=false, NavigationCompleted never fires (engine pauses).
///    Workaround: controller is positioned OFF-SCREEN at (-5000,-5000), size 1280x800.
///    User never sees it, but the engine runs full-speed.
/// 
/// 3. The WPF WebView2 in MainWindow.xaml lives in a 1px Grid (BrowserHost).
///    This ONLY provides an HwndSource for CreateCoreWebView2ControllerAsync.
///    Actual rendering is in the off-screen controller window, NOT this 1px strip.
/// 
/// 4. The "Браузер" tab (BrowserView) is a DEBUG panel (screenshots + nav controls).
///    It does NOT contain the browser — it calls BrowserService.ScreenshotAsync().
/// 
/// 5. Cursor overlay (yellow circle) uses VIRTUAL viewport coords (0-1280, 0-800).
///    It does NOT track the user's real mouse. Position is set by our JS, not by input.
/// 
/// KNOWN RISKS:
/// - Off-screen (-5000,-5000) may conflict with multi-monitor setups.
/// - 1280x800 viewport is fixed; responsive pages may render at that width.
/// - If WebView2 changes IsVisible semantics, this breaks.
/// </summary>
public static class BrowserService
{
    private static WebView2? _webView;
    private static CoreWebView2? _core;
    private static CoreWebView2Environment? _env;
    private static CoreWebView2Controller? _controller;
    private static Task<CoreWebView2>? _initTask;
    private static readonly object _lock = new();
    private static readonly string UserDataFolder =
        Path.Combine(Path.GetTempPath(), "QwenPlayground_WebView2");

    public static void Attach(WebView2 webView)
    {
        lock (_lock)
        {
            _webView = webView;
            _core = null;
            _controller = null;
            _env = null;
            _initTask = null;
        }
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

    /// <summary>
    /// Единая точка инициализации: все конкурентные вызовы (tool + кнопки debug-вкладки)
    /// ждут ОДНУ и ту же задачу init. Раньше каждый вызов при _core == null запускал
    /// своё создание Environment/Controller — два controller на одном HWND = undefined
    /// behavior и краши WebView2.
    /// </summary>
    private static async Task<CoreWebView2> GetCoreAsync()
    {
        await EnsureResumedAsync();
        Task<CoreWebView2> init;
        lock (_lock)
        {
            if (_core is { } ready) return ready;
            init = _initTask ??= InitCoreAsync();
        }
        return await init;
    }

    private static async Task<CoreWebView2> InitCoreAsync()
    {
        try
        {
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

            // 1. Create environment
            if (_env is null)
            {
                Directory.CreateDirectory(UserDataFolder);
                _env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder, null);
            }

            // 2. Get the HWND (top-level window)
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

            // 4. Position the controller OFF-SCREEN.
            // CRITICAL: IsVisible MUST be true (WebView2 won't navigate otherwise),
            // but the position is (-5000,-5000) so the user never sees the window.
            // Fixed 1280x800 viewport for consistent screenshots regardless of app window size.
            _controller.Bounds = new System.Drawing.Rectangle(-5000, -5000, 1280, 800);
            _controller.IsVisible = true;

            var core = _controller.CoreWebView2;

            // Смерть рендерера (OOM, GPU-краш): без подписки это молча пустая страница,
            // а _core остаётся кэширован — браузер мёртв навсегда. Фиксируем в общий
            // crash-лог («почему») и сбрасываем кэш: следующее обращение переинициализирует.
            core.ProcessFailed += (_, e) =>
            {
                CrashLog.LogCrash("WebView2 renderer",
                    $"ProcessFailed: kind={e.ProcessFailedKind}. " +
                    "Браузер будет переинициализирован при следующем обращении.");
                lock (_lock)
                {
                    _core = null;
                    _initTask = null;
                    try { _controller?.Close(); }
                    catch { /* контроллер и так мёртв — уборка не критична */ }
                    _controller = null;
                }
            };

            lock (_lock) { _core = core; }

            // 5. Inject pre-load scripts (run BEFORE any page script on every navigation).
            // This ensures console interceptor + cursor overlay are active from the very first moment.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ConsoleInterceptorJs);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(CursorOverlayJs);

            // 6. Enable network monitoring
            await EnableNetworkMonitoringAsync();

            // 6.5. Enable download capture (files → workspace downloads/)
            EnableDownloadCapture(core);

            // 6.6. Permissions: в агентском браузере нет пользователя, который подтвердил бы
            // «разрешить несколько автоматических загрузок» и т.п. — разрешаем автоматически.
            core.PermissionRequested += (s, e) =>
            {
                try { e.State = CoreWebView2PermissionState.Allow; } catch { }
            };

            // 7. Start auto-suspend timer (suspend after 2 min idle)
            StartSuspendTimer();

            return core;
        }
        catch
        {
            // Сбрасываем задачу, чтобы следующий вызов попробовал инициализацию заново,
            // а не кэшировал провал навсегда.
            lock (_lock) { _initTask = null; }
            throw;
        }
    }

    /// <summary>
    /// Positions the WebView2 controller OFF-SCREEN.
    /// The browser renders at 1280x800 but at (-5000,-5000) so the user never sees it.
    /// </summary>
    private static void UpdateControllerBounds(WebView2 webView)
    {
        // Controller stays off-screen always. Size is fixed at 1280x800.
        // No-op for now — the position is set once in GetCoreAsync.
    }

    /// <summary>Call when the panel is resized. No-op — controller is off-screen.</summary>
    public static void OnPanelResized()
    {
        // Controller is off-screen, no need to update bounds on resize.
    }

    /// <summary>Show or hide the native WebView2 window.</summary>
    public static void SetVisible(bool visible)
    {
        // Controller must stay visible for rendering. This is a no-op.
        // (WebView2 won't navigate if IsVisible=false)
    }

    // ─── Navigation ───────────────────────────────────────────

    public static async Task<string> NavigateAsync(string url, CancellationToken ct = default)
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
        var result = await Task.WhenAny(tcs.Task, Task.Delay(30_000, ct));

        if (ct.IsCancellationRequested)
        {
            core.NavigationCompleted -= OnCompleted;
            sw.Stop();
            return $"Navigation to {url} cancelled by user.";
        }
        if (result == tcs.Task)
        {
            var navResult = await tcs.Task;
            sw.Stop();
            var status = navResult.IsSuccess ? "OK" : $"failed (HTTP {navResult.HttpStatusCode})";
            // Install console interceptor on the new page
            await core.ExecuteScriptAsync(ConsoleInterceptorJs);
            return $"Navigated to {url} (page load: {navResult.HttpStatusCode}, total: {sw.ElapsedMilliseconds}ms) — {status}";
        }
        else
        {
            core.NavigationCompleted -= OnCompleted;
            sw.Stop();
            return $"Error: navigation to {url} timed out after 30s. Internet may be down.";
        }
    }

    /// <summary>
    /// Единая точка навигации: navigate (URL) / back / forward / reload.
    /// back/forward/reload дают короткий задержку-стабилизацию (NavigationCompleted
    /// на них не подписываемся — 500-800мс достаточно для SPA-роутинга).
    /// </summary>
    public static async Task<string> NavigateActionAsync(string action, string url, CancellationToken ct = default)
    {
        var core = await GetCoreAsync();
        switch (action.Trim().ToLowerInvariant())
        {
            case "back":
                if (!core.CanGoBack) return "Cannot go back — no previous page in history.";
                core.GoBack();
                await Task.Delay(500);
                return $"Went back → {core.Source}";
            case "forward":
                if (!core.CanGoForward) return "Cannot go forward — no next page in history.";
                core.GoForward();
                await Task.Delay(500);
                return $"Went forward → {core.Source}";
            case "reload":
                core.Reload();
                await Task.Delay(800);
                return $"Reloaded {core.Source}";
            default:
                if (string.IsNullOrWhiteSpace(url))
                    return $"Error: Action='{action}' unknown (use navigate/back/forward/reload) and no Url given.";
                return await NavigateAsync(url, ct);
        }
    }

    public static async Task<string> GetCurrentUrlAsync()
    {
        var core = await GetCoreAsync();
        return core.Source;
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

    /// <summary>
    /// Takes a screenshot of the ENTIRE page by temporarily resizing the viewport
    /// to the full document height. Resizes back to 1280x800 after capture.
    /// Returns the file path to the full-page screenshot.
    /// </summary>
    public static async Task<string> ScreenshotFullPageAsync()
    {
        var core = await GetCoreAsync();
        if (_controller is null) throw new InvalidOperationException("Controller not available.");

        // 1. Get the full page height
        var heightResult = await core.ExecuteScriptAsync(
            "Math.max(document.documentElement.scrollHeight, document.body?.scrollHeight || 0)");
        var fullHeight = int.Parse(Unquote(heightResult));
        fullHeight = Math.Min(fullHeight, 16000); // Cap at 16000px to avoid OOM
        fullHeight = Math.Max(fullHeight, 800);    // At least the normal viewport

        // 2. Resize controller to full page height
        var originalBounds = _controller.Bounds;
        _controller.Bounds = new System.Drawing.Rectangle(-5000, -5000, 1280, fullHeight);

        // 3. Wait for reflow
        await Task.Delay(500);

        // 4. Screenshot
        using var stream = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        stream.Position = 0;

        var path = Path.Combine(Path.GetTempPath(), $"browser_full_{DateTime.Now:HHmmss_fff}.png");
        using var fs = File.Create(path);
        stream.CopyTo(fs);

        // 5. Restore original viewport
        _controller.Bounds = originalBounds;

        return path;
    }

    /// <summary>
    /// Takes a series of screenshots at regular intervals. Useful for observing
    /// animations, loading states, transitions, or video playback.
    /// Returns a list of file paths (one per frame).
    /// </summary>
    public static async Task<List<string>> ScreenshotSeriesAsync(int count, int intervalMs)
    {
        var core = await GetCoreAsync();
        var paths = new List<string>();
        for (int i = 0; i < count; i++)
        {
            using var stream = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Position = 0;
            var path = Path.Combine(Path.GetTempPath(), $"browser_series_{DateTime.Now:HHmmss_fff}_{i}.png");
            using var fs = File.Create(path);
            stream.CopyTo(fs);
            paths.Add(path);
            if (i < count - 1)
                await Task.Delay(intervalMs);
        }
        return paths;
    }

    // ─── Interaction ──────────────────────────────────────────

    public static async Task<string> ClickAsync(string selector)
    {
        var core = await GetCoreAsync();
        var urlBefore = core.Source;

        // Subscribe BEFORE the click so we don't miss fast navigations
        var tcs = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnNav;
            tcs.TrySetResult(e);
        }
        core.NavigationCompleted += OnNav;

        var js = BuildClickJs(selector);
        var result = await core.ExecuteScriptAsync(js);
        var text = Unquote(result);
        if (text != "OK")
        {
            core.NavigationCompleted -= OnNav;
            return $"Click failed: {text}";
        }

        // Wait briefly to see if navigation was triggered
        var done = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        core.NavigationCompleted -= OnNav;

        if (done == tcs.Task)
            return $"Clicked {selector} → navigated to {core.Source}";
        return $"Clicked {selector}";
    }

    /// <summary>Click at specific viewport coordinates (for canvas, SVG, elements without CSS selectors).
    /// trusted=true — CDP-клик (isTrusted=true), для сайтов с антибот-проверкой.
    /// button: left/right (ПКМ), clicks: 1-3 (двойной клик).</summary>
    public static async Task<string> ClickAtAsync(int x, int y, bool trusted = false, string button = "left", int clicks = 1)
    {
        if (trusted)
            return await CdpClickAsync(x, y, button, clicks);
        var core = await GetCoreAsync();
        var urlBefore = core.Source;

        var tcs = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnNav;
            tcs.TrySetResult(e);
        }
        core.NavigationCompleted += OnNav;

        var js = BuildClickAtJs(x, y, button, clicks);
        var result = await core.ExecuteScriptAsync(js);
        var text = Unquote(result);
        if (text != "OK")
        {
            core.NavigationCompleted -= OnNav;
            return $"ClickAt failed: {text}";
        }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        core.NavigationCompleted -= OnNav;

        if (done == tcs.Task)
            return $"Clicked at ({x},{y}) → navigated to {core.Source}";
        return $"Clicked at ({x},{y})";
    }

    /// <summary>Type into an input/textarea. mode='set' — value целиком (быстро);
    /// mode='type' — посимвольно с keydown/input/keyup (React-контролируемые инпуты, debounce-поиск).</summary>
    public static async Task<string> TypeAsync(string selector, string text, string mode = "set")
    {
        var core = await GetCoreAsync();
        var js = mode.Equals("type", StringComparison.OrdinalIgnoreCase)
            ? BuildTypeCharsJs(selector, text)
            : BuildTypeJs(selector, text);
        var result = await core.ExecuteScriptAsync(js);
        var ok = Unquote(result) == "OK";
        return ok ? $"Typed '{text}' into {selector} (mode={mode})" : $"Type failed: {Unquote(result)}";
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
        return $"Scrolled by {deltaY}px (now at {await GetScrollPositionAsync()})";
    }

    public static async Task<string> GetScrollPositionAsync()
    {
        var core = await GetCoreAsync();
        var r = await core.ExecuteScriptAsync("Math.round(window.scrollY)");
        return r + "/" + await core.ExecuteScriptAsync("document.documentElement.scrollHeight");
    }

    /// <summary>Hover over an element. trusted=true — CDP mouseMoved (isTrusted=true):
    /// для реальных :hover-меню, которые не открываются от синтетического mouseover.</summary>
    public static async Task<string> HoverAsync(string selector, bool trusted = false)
    {
        var core = await GetCoreAsync();
        if (trusted)
        {
            var js = "(function() {" +
                "var el = document.querySelector(" + JsStr(selector) + ");" +
                "if (!el) return 'ERROR: not found: " + EscapeJs(selector) + "';" +
                "el.scrollIntoView({ block: 'center' });" +
                "var r = el.getBoundingClientRect();" +
                "return Math.round(r.left + r.width / 2) + ',' + Math.round(r.top + r.height / 2);" +
                "})()";
            var raw = await core.ExecuteScriptAsync(js);
            var text = Unquote(raw);
            var parts = text.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var cx) || !int.TryParse(parts[1], out var cy))
                return $"Hover failed: {text}";
            await CdpAsync("Input.dispatchMouseEvent", $"{{\"type\":\"mouseMoved\",\"x\":{cx},\"y\":{cy}}}");
            return $"Trusted hover at ({cx},{cy}) on {selector}";
        }
        var hoverJs = BuildHoverJs(selector);
        var result = await core.ExecuteScriptAsync(hoverJs);
        return Unquote(result) == "OK" ? $"Hovered {selector}" : $"Hover failed: {Unquote(result)}";
    }

    /// <summary>Wait for an element. mode='appear' (default) — дождаться появления;
    /// mode='absent' — дождаться исчезновения (спиннер ушёл, модалка закрылась).
    /// ВАЖНО: ExecuteScriptAsync НЕ дожидается JS-Promise (возвращает сам объект {}),
    /// поэтому опрос идёт на C#-стороне каждые 200мс — MutationObserver/Promise не работают.</summary>
    public static async Task<string> WaitAsync(string selector, int timeoutMs, string mode = "appear")
    {
        var core = await GetCoreAsync();
        var absent = mode.Equals("absent", StringComparison.OrdinalIgnoreCase);
        var checkJs = "(function() {" +
                      "var found = !!document.querySelector(" + JsStr(selector) + ");" +
                      "return " + (absent ? "!found" : "found") + " ? 'YES' : 'NO';" +
                      "})()";
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (true)
        {
            var result = await core.ExecuteScriptAsync(checkJs);
            if (Unquote(result) == "YES")
                return absent ? $"Element {selector} is gone" : $"Element {selector} appeared";
            if (DateTime.UtcNow >= deadline)
                return $"Timeout waiting for {selector} to {(absent ? "disappear" : "appear")} ({timeoutMs}ms)";
            await Task.Delay(200);
        }
    }

    public static async Task<string> EvaluateAsync(string script)
    {
        var core = await GetCoreAsync();
        var result = await core.ExecuteScriptAsync(script);
        return Unquote(result);
    }

    /// <summary>
    /// Read element text line-based (like file_read): returns lines offset..offset+limit-1
    /// of the joined innerText + how many lines remain. Keeps context cost predictable.
    /// </summary>
    public static async Task<string> ExtractAsync(string selector, int offset, int limit)
    {
        var core = await GetCoreAsync();
        var js = BuildExtractJs(selector);
        var result = await core.ExecuteScriptAsync(js);
        var text = Unquote(result);
        if (text.StartsWith("ERROR:", StringComparison.Ordinal)) return text;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var total = lines.Length;
        offset = Math.Clamp(offset, 0, total);
        if (offset >= total)
            return $"Nothing to read at line {offset} (content is {total} lines).";
        var end = Math.Min(offset + limit, total);
        var slice = string.Join("\n", lines.Skip(offset).Take(end - offset));
        var tail = end < total
            ? $"\n… {total - end} more lines — continue with Offset={end}."
            : "\n(end of content)";
        return $"Lines {offset}–{end - 1} of {total} for {selector}:\n{slice}\n{tail.TrimStart()}";
    }

    /// <summary>
    /// Fetch a URL in the page context — с cookie/сессией текущей страницы, БЕЗ навигации.
    /// ExecuteScriptAsync не дожидается Promise (возвращает {}), поэтому: fetch пишёт результат
    /// в window.__agent_fetch, C# поллит глобальную переменную.
    /// </summary>
    public static async Task<string> FetchAsync(string url, int timeoutMs = 30_000)
    {
        var core = await GetCoreAsync();
        await core.ExecuteScriptAsync(
            "(function() {" +
            "window.__agent_fetch = null;" +
            "fetch(" + JsStr(url) + ", { credentials: 'include' }).then(function(r) {" +
            "return r.text().then(function(t) { window.__agent_fetch = { status: r.status, body: t }; });" +
            "}).catch(function(e) { window.__agent_fetch = { error: String(e) }; });" +
            "return 'started';" +
            "})()");

        const int Cap = 50_000;
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var raw = await core.ExecuteScriptAsync(
                "window.__agent_fetch ? JSON.stringify(window.__agent_fetch) : null");
            var json = Unquote(raw);
            if (json is not null && json != "null" && json != "{}")
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    return "Fetch failed: " + err.GetString() + " (CORS? другой домен? — для других доменов webfetch или navigate)";
                var status = doc.RootElement.GetProperty("status").GetInt32();
                var body = doc.RootElement.GetProperty("body").GetString() ?? "";
                var total = body.Length;
                if (body.Length > Cap) body = body[..Cap] + $"\n… truncated ({total} chars total)";
                return $"HTTP {status} ({total} chars):\n{body}";
            }
            await Task.Delay(200);
        }
        return $"Timeout: fetch of {url} did not complete within {timeoutMs}ms (CORS? slow network?)";
    }

    /// <summary>
    /// Search for text on the page. Returns total count + per-match info: approximate scroll
    /// position (px from top of page & % of page height), visibility, element, context snippet.
    /// Non-invasive when matchIndex &lt; 0 (read-only: no DOM change, no scroll). When matchIndex
    /// &gt;= 0, scrolls that match to the viewport center and highlights it (yellow) — the caller
    /// should then take a screenshot. Returns (formatted text, whether a match was jumped to).
    /// </summary>
    public static async Task<(string Text, bool Jumped)> FindAsync(string query, bool caseSensitive, int matchIndex)
    {
        var core = await GetCoreAsync();
        var js = BuildFindJs(query, caseSensitive, matchIndex);
        var raw = await core.ExecuteScriptAsync(js);
        var json = Unquote(raw);

        FindResult? fr;
        try
        {
            fr = JsonSerializer.Deserialize<FindResult>(json, FindJsonOpts);
        }
        catch
        {
            return ("Error: could not parse find result: " + json, false);
        }
        if (fr is null)
            return ("Error: empty find result.", false);

        if (fr.total == 0)
            return ($"No matches for \"{query}\" (page {fr.docHeight}px, viewport {fr.viewH}px).", false);

        var sb = new StringBuilder();
        var totalLabel = fr.capped ? $"{fr.total}+ (showing first {fr.total})" : fr.total.ToString();
        sb.Append($"Found {totalLabel} match(es) for \"{query}\" — page {fr.docHeight}px, viewport {fr.viewH}px.\n");
        sb.Append("y = px from top of page, % = fraction of page height (so you know roughly where to scroll):\n");
        foreach (var m in fr.matches)
        {
            sb.Append($"  [{m.i}] y≈{m.y} ({m.pct}%) {(m.vis ? "" : "[hidden] ")}<{m.el}> \"{CleanSnippet(m.ctx)}\"\n");
        }
        if (fr.jumped)
            sb.Append($"→ Scrolled to match [{matchIndex}] and highlighted it (yellow). Screenshot attached.");
        else
            sb.Append("Tip: pass MatchIndex=<i> to scroll to and screenshot match <i>.");
        return (sb.ToString().TrimEnd(), fr.jumped);
    }

    /// <summary>Compact a context snippet for one line: flatten whitespace, cap length.</summary>
    private static string CleanSnippet(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        s = s.Trim();
        if (s.Length > 120) s = s[..120] + "…";
        return s;
    }

    private static readonly JsonSerializerOptions FindJsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class FindResult
    {
        public int total { get; set; }
        public bool capped { get; set; }
        public int docHeight { get; set; }
        public int viewH { get; set; }
        public bool jumped { get; set; }
        public List<FindMatch> matches { get; set; } = new();
    }

    private sealed class FindMatch
    {
        public int i { get; set; }
        public int y { get; set; }
        public int pct { get; set; }
        public bool vis { get; set; }
        public string el { get; set; } = "";
        public string ctx { get; set; } = "";
    }

    /// <summary>Press a key. If selector is provided, focuses that element first.
    /// trusted=true — CDP-событие (isTrusted=true), для сайтов с isTrusted-проверкой (Google submit).
    /// modifiers: "ctrl+shift+alt+meta" — для Ctrl+C, Ctrl+A и т.п.</summary>
    public static async Task<string> KeyAsync(string key, string? selector = null, bool trusted = false, string modifiers = "")
    {
        var core = await GetCoreAsync();
        if (trusted)
        {
            if (selector is not null)
            {
                await core.ExecuteScriptAsync(
                    "(function() {" +
                    "var el = document.querySelector(" + JsStr(selector) + ");" +
                    "if (el) { el.scrollIntoView({ block: 'center' }); el.focus(); }" +
                    "return 'OK';" +
                    "})()");
            }
            var cdpResult = await CdpKeyAsync(key, modifiers);
            return cdpResult + (selector is not null ? $" (focused {selector} first)" : "");
        }
        var keyJs = key.ToLowerInvariant() switch
        {
            "enter" => "Enter",
            "tab" => "Tab",
            "escape" or "esc" => "Escape",
            "backspace" => "Backspace",
            "delete" or "del" => "Delete",
            "space" => " ",
            "arrowup" or "up" => "ArrowUp",
            "arrowdown" or "down" => "ArrowDown",
            "arrowleft" or "left" => "ArrowLeft",
            "arrowright" or "right" => "ArrowRight",
            "home" => "Home",
            "end" => "End",
            _ => key
        };
        var js = BuildKeyJs(keyJs, selector, modifiers);
        var result = await core.ExecuteScriptAsync(js);
        var label = string.IsNullOrEmpty(modifiers) ? key : $"{modifiers}+{key}";
        return Unquote(result) == "OK" ? $"Pressed {label}{(selector is not null ? $" in {selector}" : "")}" : $"Key failed: {Unquote(result)}";
    }

    /// <summary>Move the cursor overlay to specific coordinates. Returns current element info at that point.</summary>
    public static async Task<string> CursorMoveAsync(int x, int y)
    {
        var core = await GetCoreAsync();
        var js = "(function() {" +
            "var c = document.getElementById('__agent_cursor');" +
            "if (!c) { c = document.createElement('div'); c.id = '__agent_cursor';" +
            "c.style.cssText = 'position:fixed;width:20px;height:20px;border:3px solid #ff0;border-radius:50%;pointer-events:none;z-index:2147483647;transform:translate(-50%,-50%);box-shadow:0 0 6px #ff0,0 0 12px rgba(255,255,0,0.5);';" +
            "document.body.appendChild(c); }" +
            "c.style.left = " + x + " + 'px'; c.style.top = " + y + " + 'px';" +
            "var el = document.elementFromPoint(" + x + ", " + y + ");" +
            "if (!el) return 'cursor at (" + x + "," + y + ") — empty';" +
            "var tag = el.tagName.toLowerCase();" +
            "var info = tag;" +
            "if (el.id) info += '#' + el.id;" +
            "if (el.className && typeof el.className === 'string') info += '.' + el.className.split(' ').slice(0,2).join('.');" +
            "if (el.href) info += ' href=' + el.getAttribute('href');" +
            "if (el.textContent) info += ' text=[' + el.textContent.trim().slice(0,40) + ']';" +
            "return 'cursor at (" + x + "," + y + ") — ' + info;" +
            "})()";
        var result = await core.ExecuteScriptAsync(js);
        return Unquote(result);
    }
    public static async Task<string> GetConsoleAsync()
    {
        var core = await GetCoreAsync();
        // Ensure interceptor is installed
        await core.ExecuteScriptAsync(ConsoleInterceptorJs);
        // Read the buffer
        var result = await core.ExecuteScriptAsync(
            "JSON.stringify(window.__agent_console || [])");
        return Unquote(result);
    }

    // ─── CDP (DevTools Protocol) ──────────────────────────────

    /// <summary>Call a DevTools Protocol method. Returns the JSON result.</summary>
    public static async Task<string> CdpAsync(string method, string parameters = "{}")
    {
        var core = await GetCoreAsync();
        var result = await core.CallDevToolsProtocolMethodAsync(method, parameters);
        return result;
    }

    /// <summary>
    /// Dispatch TRUSTED mouse events via CDP (isTrusted=true): left/right button, 1-3 clicks
    /// (double-click = clickCount 1→2, как в реальном браузере).
    /// </summary>
    public static async Task<string> CdpClickAsync(int x, int y, string button = "left", int clicks = 1)
    {
        var b = button.ToLowerInvariant() == "right" ? "right" : "left";
        var cc = Math.Clamp(clicks, 1, 3);
        for (var i = 1; i <= cc; i++)
        {
            await CdpAsync("Input.dispatchMouseEvent",
                $"{{\"type\":\"mousePressed\",\"x\":{x},\"y\":{y},\"button\":\"{b}\",\"clickCount\":{i}}}");
            await CdpAsync("Input.dispatchMouseEvent",
                $"{{\"type\":\"mouseReleased\",\"x\":{x},\"y\":{y},\"button\":\"{b}\",\"clickCount\":{i}}}");
            if (cc > 1 && i < cc) await Task.Delay(80);
        }
        return cc == 1
            ? $"CDP {b} click at ({x},{y})"
            : $"CDP {cc}x {b} click at ({x},{y})";
    }

    /// <summary>
    /// Dispatch a TRUSTED key event via CDP (isTrusted=true).
    /// Fixes the form-submit problem that synthetic KeyboardEvent can't solve.
    /// modifiers: "ctrl+shift+alt+meta" (CDP bitfield: Alt=1, Ctrl=2, Meta=4, Shift=8).
    /// </summary>
    public static async Task<string> CdpKeyAsync(string key, string modifiers = "")
    {
        var keyDesc = key.ToLowerInvariant() switch
        {
            "enter" => "Enter",
            "tab" => "Tab",
            "escape" => "Escape",
            "backspace" => "Backspace",
            "delete" => "Delete",
            " " or "space" => " ",
            _ => key
        };
        var keyCode = key.ToLowerInvariant() switch
        {
            "enter" => 13,
            "tab" => 9,
            "escape" => 27,
            "backspace" => 8,
            "delete" => 46,
            _ => 0
        };
        var mod = ParseModifiers(modifiers);
        var label = string.IsNullOrEmpty(modifiers) ? key : $"{modifiers}+{key}";
        // rawKeyDown (не keyDown) — CDP-канон для «настоящих» нажатий с модификаторами
        await CdpAsync("Input.dispatchKeyEvent",
            $"{{\"type\":\"rawKeyDown\",\"key\":\"{keyDesc}\",\"code\":\"{keyDesc}\",\"windowsVirtualKeyCode\":{keyCode},\"nativeVirtualKeyCode\":{keyCode},\"modifiers\":{mod}}}");
        await CdpAsync("Input.dispatchKeyEvent",
            $"{{\"type\":\"keyUp\",\"key\":\"{keyDesc}\",\"code\":\"{keyDesc}\",\"windowsVirtualKeyCode\":{keyCode},\"nativeVirtualKeyCode\":{keyCode},\"modifiers\":{mod}}}");
        return $"CDP key: {label}";
    }

    /// <summary>"ctrl+shift" → CDP bitfield (Alt=1, Ctrl=2, Meta=4, Shift=8).</summary>
    private static int ParseModifiers(string modifiers)
    {
        var m = 0;
        foreach (var p in modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            m |= p.ToLowerInvariant() switch
            {
                "ctrl" or "control" => 2,
                "alt" => 1,
                "shift" => 8,
                "meta" or "cmd" or "win" => 4,
                _ => 0
            };
        return m;
    }

    // ─── Network Diagnostics ──────────────────────────────────

    private static List<Dictionary<string, string>>? _networkLog;

    /// <summary>Enable network monitoring via WebResourceResponseReceived event.</summary>
    public static async Task EnableNetworkMonitoringAsync()
    {
        var core = await GetCoreAsync();
        _networkLog = new List<Dictionary<string, string>>();

        core.WebResourceResponseReceived += (s, e) =>
        {
            try
            {
                var uri = e.Request?.Uri ?? "";
                var status = e.Response?.StatusCode ?? 0;
                _networkLog?.Add(new Dictionary<string, string>
                {
                    // Метод реального запроса (раньше хардкодился "GET" — POST/PUT показывались GET).
                    ["method"] = e.Request?.Method ?? "GET",
                    ["url"] = uri,
                    ["status"] = status.ToString(),
                    ["type"] = ""
                });
                if (_networkLog!.Count > 200) _networkLog.RemoveAt(0);
            }
            catch { }
        };
    }

    /// <summary>Get recent network requests (for diagnostics).</summary>
    public static string GetNetworkLog()
    {
        if (_networkLog is null || _networkLog.Count == 0)
            return "No network requests captured yet. Call browser_network after navigating.";
        var recent = _networkLog.TakeLast(30);
        var lines = recent.Select(r =>
            $"{r["method"]} {r.GetValueOrDefault("status", "?")} [{r.GetValueOrDefault("type", "")}] {r["url"][..Math.Min(r["url"].Length, 100)]}");
        return $"Last {recent.Count()} requests (of {_networkLog.Count} total):\n" + string.Join("\n", lines);
    }

    // ─── Downloads / Uploads ──────────────────────────────────

    private sealed record DownloadEntry(string Path, string FileName, long Size, DateTime Time, bool Consumed = false);
    private static readonly List<DownloadEntry> _downloads = new();
    private static readonly object _downloadsLock = new();

    /// <summary>Файлы скачиваются в воркспейс (downloads/), а не в «Downloads» системы —
    /// чтобы агент мог сразу читать их другими инструментами.</summary>
    private static string DownloadDir => Path.Combine(SelfBuildPaths.WorkspaceRoot, "downloads");

    /// <summary>
    /// Перехват загрузок (API SDK 1.0.2903): DownloadStarting — переопределяем ResultFilePath
    /// в downloads/ (имя из URL, анти-коллизия), Handled=true — браузер не лезет в «Downloads».
    /// Готовность отслеживаем через operation.StateChanged (события DownloadCompleted в этой
    /// версии нет) и записываем в журнал (читает browser_download).
    /// </summary>
    private static void EnableDownloadCapture(CoreWebView2 core)
    {
        core.DownloadStarting += (s, e) =>
        {
            try
            {
                Directory.CreateDirectory(DownloadDir);
                var op = e.DownloadOperation;
                var name = "download_" + DateTime.Now.ToString("HHmmss_fff");
                try
                {
                    var seg = new Uri(op.Uri).AbsolutePath.TrimEnd('/');
                    var last = seg.Substring(seg.LastIndexOf('/') + 1);
                    if (!string.IsNullOrWhiteSpace(last)) name = Uri.UnescapeDataString(last);
                }
                catch { }
                var path = Path.Combine(DownloadDir, name);
                var i = 1;
                while (File.Exists(path))
                {
                    var dot = name.LastIndexOf('.');
                    path = Path.Combine(DownloadDir, dot > 0 ? name[..dot] + "_" + i + name[dot..] : name + "_" + i);
                    i++;
                }
                e.ResultFilePath = path;
                e.Handled = true;
                op.StateChanged += (_, _) =>
                {
                    try
                    {
                        if (op.State != CoreWebView2DownloadState.Completed) return;
                        long size = 0;
                        try { size = new FileInfo(op.ResultFilePath).Length; } catch { }
                        lock (_downloadsLock)
                        {
                            _downloads.Add(new DownloadEntry(op.ResultFilePath, Path.GetFileName(op.ResultFilePath), size, DateTime.Now));
                            if (_downloads.Count > 50) _downloads.RemoveAt(0);
                        }
                    }
                    catch { }
                };
            }
            catch { /* путь по умолчанию лучше, чем падение на скачивании */ }
        };
    }

    /// <summary>
    /// Получить результат загрузки. Агент кликает ссылку, затем вызывает инструмент.
    /// Две ветки: (1) есть незабрана́я завершённая загрузка — возвращаем её (без тайм-окна:
    /// зазор «клик → вызов» = время размышления модели, непредсказуемо); (2) ждём завершения
    /// следующей (500ms poll) — для медленных файлов, которые ещё скачиваются.
    /// </summary>
    public static async Task<string> WaitForDownloadAsync(int timeoutMs)
    {
        DownloadEntry ready;
        lock (_downloadsLock)
        {
            ready = _downloads.LastOrDefault(d => !d.Consumed);
            if (ready is not null)
            {
                var idx = _downloads.IndexOf(ready);
                _downloads[idx] = ready with { Consumed = true };
            }
        }
        if (ready is not null)
            return $"Downloaded: {ready.Path} ({ready.Size} bytes) — you can read it with other tools.";

        int before;
        lock (_downloadsLock) before = _downloads.Count;
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_downloadsLock)
            {
                if (_downloads.Count > before)
                {
                    var d = _downloads[^1];
                    return $"Downloaded: {d.Path} ({d.Size} bytes) — you can read it with other tools.";
                }
            }
            await Task.Delay(500);
        }
        return $"No download completed within {timeoutMs}ms. Click the download link/button first, then call browser_download.";
    }

    /// <summary>
    /// Upload a file into an <input type="file"> via CDP DOM.setFileInputFiles — trusted,
    /// сайт не отличает от реального выбора. nodeId берём из DOM.getDocument + DOM.querySelector.
    /// </summary>
    public static async Task<string> UploadAsync(string selector, string filePath)
    {
        try
        {
            return await UploadCoreAsync(selector, filePath);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static async Task<string> UploadCoreAsync(string selector, string filePath)
    {
        if (!File.Exists(filePath)) return $"Error: file not found: {filePath}";
        var abs = Path.GetFullPath(filePath);

        // objectId элемента через Runtime.evaluate (DOM.querySelector в CDP-субмножестве
        // WebView2 не поддерживается — бросает ArgumentException).
        // expression строится как JS (JsStr = JS-строковый литерал), затем сериализуется
        // в JSON целиком — иначе кавычки из JsStr ломают JSON-обёртку (WebView2 бросает
        // ArgumentException на битом JSON).
        var expression = "document.querySelector(" + JsStr(selector) + ")";
        var evalParams = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["expression"] = expression,
            ["returnByValue"] = false
        });
        var ev = await CdpAsync("Runtime.evaluate", evalParams);
        using (var evJson = JsonDocument.Parse(ev))
        {
            if (evJson.RootElement.TryGetProperty("exceptionDetails", out _))
                return $"Error: JS exception while querying {selector}";
            var result = evJson.RootElement.GetProperty("result");
            if (!result.TryGetProperty("objectId", out var oid))
                return $"Error: not found: {selector}";
            await CdpAsync("DOM.setFileInputFiles",
                $"{{\"files\":[{JsStr(abs)}],\"objectId\":{JsStr(oid.GetString())}}}");
        }

        // change-событие — чтобы React/формы заметили файл.
        var core = await GetCoreAsync();
        await core.ExecuteScriptAsync(
            "(function() {" +
            "var el = document.querySelector(" + JsStr(selector) + ");" +
            "if (el) el.dispatchEvent(new Event('change', { bubbles: true }));" +
            "return 'OK';" +
            "})()");
        return $"Uploaded {Path.GetFileName(abs)} to {selector}";
    }

    // ─── Auto-Suspend ─────────────────────────────────────────

    private static DateTime _lastActivity = DateTime.Now;
    private static bool _suspendTimerStarted;

    /// <summary>Call on every browser tool use to reset the idle timer.</summary>
    public static void TouchActivity()
    {
        _lastActivity = DateTime.Now;
    }

    /// <summary>Start the auto-suspend timer. Call once at init.</summary>
    public static void StartSuspendTimer()
    {
        if (_suspendTimerStarted) return;
        _suspendTimerStarted = true;
        var timer = new System.Timers.Timer(120_000) { AutoReset = true }; // 2 min
        timer.Elapsed += async (_, _) =>
        {
            // Всё тело в try: таймер живёт на потоке пула, и любое исключение
            // (например IsSuspended на disposed core после закрытия окна) без
            // обработчика убивает процесс.
            try
            {
                var core = _core;
                if ((DateTime.Now - _lastActivity).TotalMinutes >= 2 && core is not null && !core.IsSuspended)
                {
                    await core.TrySuspendAsync();
                }
            }
            catch
            {
            }
        };
        timer.Start();
    }

    /// <summary>Resume the browser if suspended. Call before any operation.</summary>
    public static async Task EnsureResumedAsync()
    {
        if (_core is not null && _core.IsSuspended)
        {
            _core.Resume();
            await Task.Delay(200); // Give it a moment to wake up
        }
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

    /// <summary>
    /// Type character-by-character: keydown → native value setter (React-совместимо) → input → keyup
    /// на каждый символ. Нативный setter (Object.getOwnPropertyDescriptor(...).set) нужен, чтобы
    /// React-контролируемые инпуты приняли значение — прямое el.value = ... React не видит.
    /// </summary>
    private static string BuildTypeCharsJs(string selector, string text) =>
        "(function() {" +
        "var el = document.querySelector(" + JsStr(selector) + ");" +
        "if (!el) return 'ERROR: not found: " + EscapeJs(selector) + "';" +
        "el.scrollIntoView({ block: 'center' });" +
        "el.focus();" +
        "var text = " + JsStr(text) + ";" +
        "function appendChar(ch) {" +
        "var proto = el.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;" +
        "var desc = Object.getOwnPropertyDescriptor(proto, 'value');" +
        "if (desc && desc.set) desc.set.call(el, el.value + ch); else el.value = el.value + ch;" +
        "el.dispatchEvent(new Event('input', { bubbles: true }));" +
        "}" +
        "for (var i = 0; i < text.length; i++) {" +
        "el.dispatchEvent(new KeyboardEvent('keydown', { key: text[i], bubbles: true }));" +
        "appendChar(text[i]);" +
        "el.dispatchEvent(new KeyboardEvent('keyup', { key: text[i], bubbles: true }));" +
        "}" +
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



    private static string BuildExtractJs(string selector) =>
        "(function() {" +
        "var els = document.querySelectorAll(" + JsStr(selector) + ");" +
        "if (els.length === 0) return 'ERROR: no elements';" +
        "var parts = [];" +
        "els.forEach(function(el){ parts.push(el.innerText); });" +
        "return parts.join('\\n---\\n');" +
        "})()";

    private const int FindCap = 50;

    /// <summary>
    /// Walk all text nodes, find every occurrence of <paramref name="query"/>, and for each
    /// record its absolute document Y (rect.top + scrollY), % of page height, visibility,
    /// element descriptor and a context snippet. If matchIndex &gt;= 0, scroll that match to the
    /// viewport center and wrap it in a yellow &lt;mark&gt;. Returns a JSON string.
    /// </summary>
    private static string BuildFindJs(string query, bool caseSensitive, int matchIndex) =>
        "(function() {" +
        "var query = " + JsStr(query) + ";" +
        "var caseSensitive = " + (caseSensitive ? "true" : "false") + ";" +
        "var matchIndex = " + matchIndex + ";" +
        "var CAP = " + FindCap + ";" +
        "var prev = document.querySelectorAll('#__agent_find_mark');" +
        "for (var k = 0; k < prev.length; k++) { if (prev[k].parentNode) prev[k].parentNode.removeChild(prev[k]); }" +
        "var docHeight = Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0);" +
        "var viewH = window.innerHeight;" +
        "var results = [];" +
        "var ranges = [];" +
        "if (document.body) {" +
        // Skip text inside non-rendered elements (script/style/noscript/template): raw code,
        // never visible page text — pure noise for a "where to scroll" search.
        "var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {" +
        "acceptNode: function(n) {" +
        "var p = n.parentElement;" +
        "if (p) { var t = p.tagName; if (t === 'SCRIPT' || t === 'STYLE' || t === 'NOSCRIPT' || t === 'TEMPLATE') return NodeFilter.FILTER_REJECT; }" +
        "return NodeFilter.FILTER_ACCEPT;" +
        "}});" +
        "var node;" +
        "while ((node = walker.nextNode()) && results.length < CAP) {" +
        "var text = node.nodeValue || '';" +
        "if (!text) continue;" +
        "var hay = caseSensitive ? text : text.toLowerCase();" +
        "var needle = caseSensitive ? query : query.toLowerCase();" +
        "var idx = 0;" +
        "while (true) {" +
        "if (results.length >= CAP) break;" +
        "var found = hay.indexOf(needle, idx);" +
        "if (found === -1) break;" +
        "var range = document.createRange();" +
        "range.setStart(node, found);" +
        "range.setEnd(node, found + query.length);" +
        "var rect = range.getBoundingClientRect();" +
        "var absTop = rect.top + window.scrollY;" +
        "var snippet = text.slice(Math.max(0, found - 40), found + query.length + 40);" +
        "var el = node.parentElement;" +
        "var desc = el ? el.tagName.toLowerCase() : '';" +
        "if (el && el.id) desc += '#' + el.id;" +
        "if (el && el.className && typeof el.className === 'string') desc += '.' + el.className.split(' ').slice(0,2).join('.');" +
        "results.push({ i: results.length, y: Math.round(absTop), pct: docHeight > 0 ? Math.round(absTop / docHeight * 100) : 0, vis: (rect.width > 0 || rect.height > 0), el: desc, ctx: snippet });" +
        "ranges.push(range);" +
        "idx = found + 1;" +
        "}" +
        "}" +
        "}" +
        "var jumped = false;" +
        "if (matchIndex >= 0 && matchIndex < results.length) {" +
        "var r = ranges[matchIndex];" +
        "var mark = document.createElement('mark');" +
        "mark.id = '__agent_find_mark';" +
        "mark.style.cssText = 'background:#ff0;box-shadow:0 0 0 2px #f0f;';" +
        "try { r.surroundContents(mark); } catch (e) {}" +
        "var targetY = results[matchIndex].y - Math.floor(viewH / 2);" +
        "window.scrollTo(0, Math.max(0, targetY));" +
        "jumped = true;" +
        "}" +
        "return JSON.stringify({ total: results.length, capped: results.length >= CAP, docHeight: docHeight, viewH: viewH, jumped: jumped, matches: results });" +
        "})()";

    private static string BuildKeyJs(string key, string? selector, string modifiers = "")
    {
        var focusPart = selector is not null
            ? "var el = document.querySelector(" + JsStr(selector) + "); if (el) el.focus();"
            : "var el = document.activeElement || document.body;";
        var modInit = ModJsInit(modifiers);
        return "(function() {" +
            focusPart +
            "el.dispatchEvent(new KeyboardEvent('keydown', { key: " + JsStr(key) + ", bubbles: true" + modInit + " }));" +
            "el.dispatchEvent(new KeyboardEvent('keypress', { key: " + JsStr(key) + ", bubbles: true" + modInit + " }));" +
            "el.dispatchEvent(new KeyboardEvent('keyup', { key: " + JsStr(key) + ", bubbles: true" + modInit + " }));" +
            "return 'OK';" +
            "})()";
    }

    /// <summary>"ctrl+shift" → ", ctrlKey: true, shiftKey: true" для init-объекта KeyboardEvent.</summary>
    private static string ModJsInit(string modifiers)
    {
        var parts = modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant()).ToList();
        var sb = new StringBuilder();
        if (parts.Contains("ctrl") || parts.Contains("control")) sb.Append(", ctrlKey: true");
        if (parts.Contains("shift")) sb.Append(", shiftKey: true");
        if (parts.Contains("alt")) sb.Append(", altKey: true");
        if (parts.Contains("meta") || parts.Contains("cmd") || parts.Contains("win")) sb.Append(", metaKey: true");
        return sb.ToString();
    }

    private static string BuildClickAtJs(int x, int y, string button, int clicks)
    {
        var btn = button.ToLowerInvariant() == "right" ? 2 : 0;
        var cc = Math.Clamp(clicks, 1, 3);
        return "(function() {" +
            "var el = document.elementFromPoint(" + x + ", " + y + ");" +
            "if (!el) return 'ERROR: no element at (" + x + "," + y + ")';" +
            "var opts = { bubbles: true, clientX: " + x + ", clientY: " + y + ", button: " + btn + " };" +
            "for (var i = 0; i < " + cc + "; i++) {" +
            "el.dispatchEvent(new MouseEvent('mousedown', opts));" +
            "el.dispatchEvent(new MouseEvent('mouseup', opts));" +
            // Последний клик: dblclick при двойном, contextmenu при ПКМ.
            "el.dispatchEvent(new MouseEvent(" + (cc > 1 ? "i === 1 ? 'dblclick' : 'click'" : (btn == 2 ? "'contextmenu'" : "'click'")) + ", opts));" +
            "}" +
            "return 'OK';" +
            "})()";
    }

    private const string ConsoleInterceptorJs =
        "(function() {" +
        "if (window.__agent_console) return 'OK';" +
        "window.__agent_console = [];" +
        "var orig = { log: console.log, warn: console.warn, error: console.error, info: console.info };" +
        "function cap(level) { return function() {" +
        "var msg = Array.from(arguments).map(function(a) {" +
        "try { return typeof a === 'object' ? JSON.stringify(a) : String(a); } catch(e) { return String(a); }" +
        "}).join(' ');" +
        "window.__agent_console.push({ level: level, msg: msg, t: Date.now() });" +
        "if (window.__agent_console.length > 100) window.__agent_console.shift();" +
        "orig[level].apply(console, arguments);" +
        "}; };" +
        "console.log = cap('log'); console.warn = cap('warn'); console.error = cap('error'); console.info = cap('info');" +
        "return 'OK';" +
        "})()";

    private const string CursorOverlayJs =
        "(function() {" +
        "if (document.getElementById('__agent_cursor')) return 'OK';" +
        "var c = document.createElement('div');" +
        "c.id = '__agent_cursor';" +
        "c.style.cssText = 'position:fixed;width:24px;height:24px;border:3px solid #fff;border-radius:50%;pointer-events:none;z-index:2147483647;transform:translate(-50%,-50%);background:rgba(255,0,255,0.3);box-shadow:0 0 4px #fff,0 0 8px #ff00ff,0 0 16px rgba(255,0,255,0.4);transition:left 0.1s,top 0.1s;';" +
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
