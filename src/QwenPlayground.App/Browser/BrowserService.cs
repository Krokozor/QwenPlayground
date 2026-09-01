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

    public static async Task GoBackAsync()
    {
        var core = await GetCoreAsync();
        if (core.CanGoBack) core.GoBack();
    }

    public static async Task GoForwardAsync()
    {
        var core = await GetCoreAsync();
        if (core.CanGoForward) core.GoForward();
    }

    public static async Task ReloadAsync()
    {
        var core = await GetCoreAsync();
        core.Reload();
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

    /// <summary>Click at specific viewport coordinates (for canvas, SVG, elements without CSS selectors).</summary>
    public static async Task<string> ClickAtAsync(int x, int y)
    {
        var core = await GetCoreAsync();
        var urlBefore = core.Source;

        var tcs = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnNav;
            tcs.TrySetResult(e);
        }
        core.NavigationCompleted += OnNav;

        var js = BuildClickAtJs(x, y);
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

    public static async Task<string> TypeAsync(string selector, string text)
    {
        var core = await GetCoreAsync();
        var js = BuildTypeJs(selector, text);
        var result = await core.ExecuteScriptAsync(js);
        return Unquote(result) == "OK" ? $"Typed '{text}' into {selector}" : $"Type failed: {Unquote(result)}";
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
        return Unquote(result) == "OK" ? $"Element {selector} appeared" : $"Timeout waiting for {selector} ({timeoutMs}ms)";
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

    /// <summary>Press a key. If selector is provided, focuses that element first.</summary>
    public static async Task<string> KeyAsync(string key, string? selector = null)
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
            "arrowup" or "up" => "ArrowUp",
            "arrowdown" or "down" => "ArrowDown",
            "arrowleft" or "left" => "ArrowLeft",
            "arrowright" or "right" => "ArrowRight",
            "home" => "Home",
            "end" => "End",
            _ => key
        };
        var js = BuildKeyJs(keyJs, selector);
        var result = await core.ExecuteScriptAsync(js);
        return Unquote(result) == "OK" ? $"Pressed {key}{(selector is not null ? $" in {selector}" : "")}" : $"Key failed: {Unquote(result)}";
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
    /// Dispatch a TRUSTED mouse event via CDP (isTrusted=true).
    /// This bypasses the synthetic event limitation — works for form submits, etc.
    /// </summary>
    public static async Task<string> CdpClickAsync(int x, int y)
    {
        // MousePressed
        await CdpAsync("Input.dispatchMouseEvent",
            $"{{\"type\":\"mousePressed\",\"x\":{x},\"y\":{y},\"button\":\"left\",\"clickCount\":1}}");
        // MouseReleased
        await CdpAsync("Input.dispatchMouseEvent",
            $"{{\"type\":\"mouseReleased\",\"x\":{x},\"y\":{y},\"button\":\"left\",\"clickCount\":1}}");
        return $"CDP click at ({x},{y})";
    }

    /// <summary>
    /// Dispatch a TRUSTED key event via CDP (isTrusted=true).
    /// Fixes the form-submit problem that synthetic KeyboardEvent can't solve.
    /// </summary>
    public static async Task<string> CdpKeyAsync(string key)
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
        // keyDown
        await CdpAsync("Input.dispatchKeyEvent",
            $"{{\"type\":\"keyDown\",\"key\":\"{keyDesc}\",\"code\":\"{keyDesc}\",\"windowsVirtualKeyCode\":{keyCode},\"nativeVirtualKeyCode\":{keyCode}}}");
        // keyUp
        await CdpAsync("Input.dispatchKeyEvent",
            $"{{\"type\":\"keyUp\",\"key\":\"{keyDesc}\",\"code\":\"{keyDesc}\",\"windowsVirtualKeyCode\":{keyCode},\"nativeVirtualKeyCode\":{keyCode}}}");
        return $"CDP key: {key}";
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
                    ["method"] = "GET",
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

    private static string BuildKeyJs(string key, string? selector)
    {
        var focusPart = selector is not null
            ? "var el = document.querySelector(" + JsStr(selector) + "); if (el) el.focus();"
            : "var el = document.activeElement || document.body;";
        return "(function() {" +
            focusPart +
            "el.dispatchEvent(new KeyboardEvent('keydown', { key: " + JsStr(key) + ", bubbles: true }));" +
            "el.dispatchEvent(new KeyboardEvent('keypress', { key: " + JsStr(key) + ", bubbles: true }));" +
            "el.dispatchEvent(new KeyboardEvent('keyup', { key: " + JsStr(key) + ", bubbles: true }));" +
            "return 'OK';" +
            "})()";
    }

    private static string BuildClickAtJs(int x, int y) =>
        "(function() {" +
        "var el = document.elementFromPoint(" + x + ", " + y + ");" +
        "if (!el) return 'ERROR: no element at (" + x + "," + y + ")';" +
        "el.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, clientX: " + x + ", clientY: " + y + " }));" +
        "el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: " + x + ", clientY: " + y + " }));" +
        "el.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: " + x + ", clientY: " + y + " }));" +
        "return 'OK';" +
        "})()";

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
