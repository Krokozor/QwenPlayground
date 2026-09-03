using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Tools;

/// <summary>
/// Управление собственным окном приложения: развернуть на весь экран / вернуть в окно /
/// сфокусировать поверх остального. Закрывает боль UI-самотестирования: после rebuild_self
/// окно всегда мелкое, и скриншоты неполные — maximize даёт полный кадр для screenshot.
///
/// Регистрация — автоматическая (рефлексия [Tool] по App-ассембли в ToolRegistry).
/// </summary>
[Tool("app_window",
    "Control your own app window: maximize to full screen, restore to windowed, or focus it on " +
    "top of everything. The window is small right after a rebuild/restart — call 'maximize' first " +
    "so the following screenshot captures the full UI. Actions: maximize | restore | focus.")]
public sealed class AppWindowTool : AgentTool
{
    [ToolParameter(
        "Action: 'maximize' (full screen), 'restore' (windowed size), or 'focus' (bring to front, keep size).",
        Required = true)]
    public string Action { get; set; } = string.Empty;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var app = Application.Current;
        if (app is null)
        {
            return Task.FromResult("Error: no WPF application is available.");
        }

        // Свойства окна трогать только на UI-потоке; Invoke из UI-потока исполняется inline.
        var result = app.Dispatcher.Invoke(() =>
        {
            if (app.MainWindow is not { } window)
            {
                return "Error: the main window was not found.";
            }

            switch (Action.Trim().ToLowerInvariant())
            {
                case "maximize":
                    window.WindowState = WindowState.Maximized;
                    BringToFront(window);
                    return "Window maximized (full screen) and focused. Take a screenshot to see the full UI.";
                case "restore":
                    window.WindowState = WindowState.Normal;
                    BringToFront(window);
                    return "Window restored to windowed size and focused.";
                case "focus":
                    BringToFront(window);
                    return "Window focused (brought to front, size unchanged).";
                default:
                    return $"Error: unknown action '{Action}'. Use: maximize | restore | focus.";
            }
        });
        return Task.FromResult(result);
    }

    /// <summary>
    /// Надёжно поднять окно наверх: трюк Topmost=true→false (окно всплывает над всеми, затем
    /// перестаёт быть topmost — не мешает остальным окнам) + Activate + SetForegroundWindow.
    /// </summary>
    private static void BringToFront(Window window)
    {
        window.Topmost = true;
        window.Topmost = false;
        window.Activate();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            SetForegroundWindow(handle);
        }
    }
}
