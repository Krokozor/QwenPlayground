using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.App.Desktop;

/// <summary>
/// Small topmost overlay window (96×72) showing the Qwen cursor on the desktop.
/// 
/// Pipeline: before every agent action (click, type, screenshot, etc.) the overlay
/// is HIDDEN so it doesn't interfere. After the action, it's SHOWN at the new position.
/// 
/// Toggle drag: user clicks to grab, moves, clicks to drop.
/// </summary>
public static class DesktopOverlay
{
    private const int CursorW = 96;
    private const int CursorH = 72;
    private const int TipX = 34;
    private const int TipY = 15;

    private static Window? _window;
    private static Image? _cursorImage;
    private static bool _initialized;
    private static bool _dragActive;
    private static Point _dragStartScreen;
    private static int _dragStartAgentX, _dragStartAgentY;

    public static int AgentX { get; private set; }
    public static int AgentY { get; private set; }

    /// <summary>Fired on UI thread when position changes.</summary>
    public static event Action? PositionChanged;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    /// <summary>
    /// Set position and show the overlay. Called AFTER an action completes.
    /// </summary>
    public static void MoveTo(int x, int y)
    {
        AgentX = x;
        AgentY = y;
        Show();
        RaiseChanged();
    }

    /// <summary>User dragged the indicator to a new position.</summary>
    public static void SetPosition(int x, int y)
    {
        AgentX = x;
        AgentY = y;
        RaiseChanged();
    }

    /// <summary>
    /// HIDE the overlay window immediately. Called BEFORE every agent action
    /// (click, type, key, scroll, drag, screenshot) so the overlay doesn't
    /// intercept the agent's own input.
    /// </summary>
    public static void Hide()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) _window?.Hide();
        else d.Invoke(() => _window?.Hide());
    }

    /// <summary>
    /// SHOW the overlay window at the current agent position.
    /// </summary>
    public static void Show()
    {
        if (!AppSettings.Get().DesktopCursorOverlay) return;
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) ShowCore();
        else d.BeginInvoke(ShowCore);
    }

    private static void ShowCore()
    {
        if (!_initialized) InitCore();
        if (_window is not null)
        {
            _window.Left = AgentX - TipX;
            _window.Top = AgentY - TipY;
            _window.Show();
        }
    }

    private static void InitCore()
    {
        _initialized = true;

        var path = Path.Combine(
            QwenPlayground.Core.SelfBuild.SelfBuildPaths.WorkspaceRoot,
            "assets", "QwenCursor_96.png");
        if (!File.Exists(path)) return;

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            Width = CursorW,
            Height = CursorH,
            Left = AgentX - TipX,
            Top = AgentY - TipY,
        };

        _cursorImage = new Image
        {
            Source = new BitmapImage(new Uri(path)),
            Width = CursorW,
            Height = CursorH,
            Cursor = Cursors.SizeAll,
        };

        _cursorImage.MouseLeftButtonDown += OnToggleClick;
        _window.Content = _cursorImage;
        _window.MouseMove += OnWindowMouseMove;
        _window.Show();
    }

    // ── Toggle drag ──

    private static void OnToggleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_dragActive)
        {
            _dragActive = true;
            GetCursorPos(out var pt);
            _dragStartScreen = new Point(pt.X, pt.Y);
            _dragStartAgentX = AgentX;
            _dragStartAgentY = AgentY;
            if (_cursorImage is not null) _cursorImage.Opacity = 0.7;
        }
        else
        {
            _dragActive = false;
            if (_cursorImage is not null) _cursorImage.Opacity = 1.0;
            RaiseChanged();
        }
    }

    private static void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragActive) return;
        GetCursorPos(out var pt);
        AgentX = _dragStartAgentX + (pt.X - (int)_dragStartScreen.X);
        AgentY = _dragStartAgentY + (pt.Y - (int)_dragStartScreen.Y);
        if (_window is not null)
        {
            _window.Left = AgentX - TipX;
            _window.Top = AgentY - TipY;
        }
    }

    private static void RaiseChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) PositionChanged?.Invoke();
        else d.BeginInvoke(() => PositionChanged?.Invoke());
    }
}
