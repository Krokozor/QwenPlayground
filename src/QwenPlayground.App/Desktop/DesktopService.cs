using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.App.Desktop;

/// <summary>
/// Win32 desktop control: mouse, keyboard, screenshots, window management.
/// Mirrors BrowserService architecture: static class, all operations are async wrappers
/// around synchronous Win32 P/Invoke calls (offloaded to thread pool for heavy ops).
///
/// Cursor overlay: after a screenshot, the Qwen cursor PNG is drawn at the action point
/// so the model can see exactly where it clicked/moved. The cursor tip (hotspot) is at
/// pixel (34, 15) in the 96×72 scaled image.
/// </summary>
public static class DesktopService
{
    // ─── Win32 P/Invoke ───────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    // mouse_event flags
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    // SendInput for keyboard
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // Virtual key codes
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_MENU = 0x12; // Alt
    private const ushort VK_LWIN = 0x5B;

    // System metrics
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    // ─── Cursor Overlay ───────────────────────────────────────

    /// <summary>Hotspot (tip) of the cursor in the 96×72 scaled image.</summary>
    private static readonly Point CursorTip = new(34, 15);
    private const int CursorWidth = 96;
    private const int CursorHeight = 72;

    private static Bitmap? _cursorBitmap;
    private static readonly object _cursorLock = new();

    private static Bitmap GetCursorBitmap()
    {
        lock (_cursorLock)
        {
            if (_cursorBitmap is not null) return _cursorBitmap;
            var path = Path.Combine(SelfBuildPaths.WorkspaceRoot, "assets", "QwenCursor_96.png");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Cursor image not found: {path}");
            _cursorBitmap = new Bitmap(path);
            return _cursorBitmap;
        }
    }

    // ─── Screenshot ───────────────────────────────────────────

    /// <summary>
    /// Capture the primary screen (or virtual screen) to a PNG file.
    /// If cursorX/cursorY are provided, the Qwen cursor is drawn at that point
    /// so the model can see where the action happened.
    /// </summary>
    public static async Task<string> ScreenshotAsync(int? cursorX = null, int? cursorY = null)
    {
        // Hide overlay so it doesn't intercept the agent's own input
        DesktopOverlay.Hide();
        await Task.Delay(30);

        var path = await Task.Run(() =>
        {
            // Virtual screen bounds (all monitors)
            var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var h = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(new Point(x, y), Point.Empty, new Size(w, h));
            }

            // Draw cursor overlay if position provided
            if (cursorX is int cx && cursorY is int cy)
            {
                var cursor = GetCursorBitmap(); // cached — do NOT dispose
                using var graphics = Graphics.FromImage(bitmap);
                graphics.DrawImage(cursor, cx - CursorTip.X, cy - CursorTip.Y, CursorWidth, CursorHeight);
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"desktop_{DateTime.Now:HHmmss_fff}.png");
            bitmap.Save(filePath, ImageFormat.Png);
            return filePath;
        });

        // Show the overlay again at the action point
        DesktopOverlay.MoveTo(cursorX ?? 0, cursorY ?? 0);
        return path;
    }

    /// <summary>
    /// Capture the screen with a coordinate grid overlay (lines every 200px + labels).
    /// Use for precise targeting: read coordinates from the grid, then desktop_move/desktop_click.
    /// </summary>
    public static async Task<string> GridScreenshotAsync()
    {
        DesktopOverlay.Hide();
        await Task.Delay(30);

        var path = await Task.Run(() =>
        {
            var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var h = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(new Point(x, y), Point.Empty, new Size(w, h));
            }

            // Draw grid
            using (var g = Graphics.FromImage(bitmap))
            {
                var linePen = new Pen(Color.FromArgb(80, 0, 200, 255), 1);
                var labelBrush = new SolidBrush(Color.FromArgb(200, 0, 200, 255));
                var font = new Font("Consolas", 10, FontStyle.Bold);
                var bgBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0));

                const int step = 200;

                // Vertical lines + X labels
                for (int gx = 0; gx <= w; gx += step)
                {
                    g.DrawLine(linePen, gx, 0, gx, h);
                    var label = gx.ToString();
                    var sz = g.MeasureString(label, font);
                    g.FillRectangle(bgBrush, gx + 2, 2, sz.Width + 4, sz.Height + 2);
                    g.DrawString(label, font, labelBrush, gx + 4, 2);
                }

                // Horizontal lines + Y labels
                for (int gy = 0; gy <= h; gy += step)
                {
                    g.DrawLine(linePen, 0, gy, w, gy);
                    var label = gy.ToString();
                    var sz = g.MeasureString(label, font);
                    g.FillRectangle(bgBrush, 2, gy + 2, sz.Width + 4, sz.Height + 2);
                    g.DrawString(label, font, labelBrush, 2, gy + 4);
                }

                linePen.Dispose();
                labelBrush.Dispose();
                font.Dispose();
                bgBrush.Dispose();
            }

            var filePath = Path.Combine(Path.GetTempPath(), $"desktop_grid_{DateTime.Now:HHmmss_fff}.png");
            bitmap.Save(filePath, ImageFormat.Png);
            return filePath;
        });

        DesktopOverlay.Show();
        return path;
    }

    /// <summary>Capture a specific window by handle to a PNG file.</summary>
    public static async Task<string> ScreenshotWindowAsync(IntPtr windowHandle)
    {
        return await Task.Run(() =>
        {
            if (!GetWindowRect(windowHandle, out var rect))
                throw new InvalidOperationException("Cannot get window rect.");

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0)
                throw new InvalidOperationException("Window has zero size.");

            using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(new Point(rect.Left, rect.Top), Point.Empty, new Size(w, h));
            }

            var path = Path.Combine(Path.GetTempPath(), $"desktop_win_{DateTime.Now:HHmmss_fff}.png");
            bitmap.Save(path, ImageFormat.Png);
            return path;
        });
    }

    // ─── Mouse ────────────────────────────────────────────────

    /// <summary>Move the cursor to screen coordinates (x, y).</summary>
    public static void MoveCursor(int x, int y)
    {
        DesktopOverlay.Hide();
        SetCursorPos(x, y);
        DesktopOverlay.MoveTo(x, y);
    }

    /// <summary>Get the current cursor position.</summary>
    public static Point GetCursorPosition()
    {
        GetCursorPos(out var pt);
        return new Point(pt.X, pt.Y);
    }

    /// <summary>
    /// Click at screen coordinates. Moves cursor first, then clicks.
    /// button: "left" (default), "right", "middle".
    /// clicks: 1 (default), 2 (double-click), 3 (triple-click).
    /// </summary>
    public static async Task<string> ClickAsync(int x, int y, string button = "left", int clicks = 1)
    {
        DesktopOverlay.Hide();
        clicks = Math.Clamp(clicks, 1, 3);
        uint down, up;
        switch (button.ToLowerInvariant())
        {
            case "right": down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; break;
            case "middle": down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; break;
            default: down = MOUSEEVENTF_LEFTDOWN; up = MOUSEEVENTF_LEFTUP; break;
        }

        await Task.Run(() =>
        {
            SetCursorPos(x, y);
            Thread.Sleep(50); // Let the cursor settle
            for (int i = 0; i < clicks; i++)
            {
                mouse_event(down, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(30);
                mouse_event(up, 0, 0, 0, UIntPtr.Zero);
                if (i < clicks - 1) Thread.Sleep(80);
            }
        });

        DesktopOverlay.MoveTo(x, y);
        return $"{button} click ×{clicks} at ({x}, {y})";
    }

    /// <summary>Scroll at screen coordinates. deltaY: positive = down, negative = up.</summary>
    public static async Task<string> ScrollAsync(int x, int y, int deltaY)
    {
        DesktopOverlay.Hide();
        await Task.Run(() =>
        {
            SetCursorPos(x, y);
            Thread.Sleep(50);
            // WHEEL: dwData is the scroll amount (positive = up, negative = down)
            // We invert because our API is positive = down
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)(ushort)(-deltaY * 120), UIntPtr.Zero);
        });
        DesktopOverlay.MoveTo(x, y);
        return $"Scrolled {deltaY}px at ({x}, {y})";
    }

    /// <summary>
    /// Drag from (x1, y1) to (x2, y2) with the given button.
    /// </summary>
    public static async Task<string> DragAsync(int x1, int y1, int x2, int y2, string button = "left")
    {
        DesktopOverlay.Hide();
        uint down, up;
        switch (button.ToLowerInvariant())
        {
            case "right": down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; break;
            case "middle": down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; break;
            default: down = MOUSEEVENTF_LEFTDOWN; up = MOUSEEVENTF_LEFTUP; break;
        }

        await Task.Run(() =>
        {
            SetCursorPos(x1, y1);
            Thread.Sleep(50);
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(100);

            // Move in steps for a smoother drag
            const int steps = 10;
            for (int i = 1; i <= steps; i++)
            {
                var ix = x1 + (x2 - x1) * i / steps;
                var iy = y1 + (y2 - y1) * i / steps;
                SetCursorPos(ix, iy);
                Thread.Sleep(20);
            }

            Thread.Sleep(50);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        });
        DesktopOverlay.MoveTo(x2, y2);
        return $"Dragged {button} button from ({x1}, {y1}) to ({x2}, {y2})";
    }

    // ─── Keyboard ─────────────────────────────────────────────

    /// <summary>
    /// Type text using Unicode input (layout-independent).
    /// Each character is sent as a keydown+keyup pair with KEYEVENTF_UNICODE.
    /// </summary>
    public static async Task<string> TypeTextAsync(string text)
    {
        DesktopOverlay.Hide();
        var pos = GetCursorPosition();
        await Task.Run(() =>
        {
            var inputs = new List<INPUT>();
            foreach (var ch in text)
            {
                inputs.Add(MakeUnicodeInput(ch, keyUp: false));
                inputs.Add(MakeUnicodeInput(ch, keyUp: true));
            }
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        });
        DesktopOverlay.MoveTo(pos.X, pos.Y);
        return $"Typed {text.Length} chars: \"{Truncate(text, 60)}\"";
    }

    /// <summary>
    /// Press a key or key combination.
    /// key: "enter", "tab", "escape", "backspace", "delete", "space", "arrowup", etc., or a single char.
    /// modifiers: "ctrl", "shift", "alt", "win", or combos like "ctrl+shift".
    /// </summary>
    public static async Task<string> PressKeyAsync(string key, string modifiers = "")
    {
        DesktopOverlay.Hide();
        var pos = GetCursorPosition();
        await Task.Run(() =>
        {
            var inputs = new List<INPUT>();

            // Press modifiers down
            foreach (var mod in ParseModifiers(modifiers))
            {
                inputs.Add(MakeVkInput(mod, keyUp: false));
            }

            // Press the main key
            var vk = KeyToVk(key);
            if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
            {
                // Single character: use Unicode
                inputs.Add(MakeUnicodeInput(key[0], keyUp: false));
                inputs.Add(MakeUnicodeInput(key[0], keyUp: true));
            }
            else
            {
                inputs.Add(MakeVkInput(vk, keyUp: false));
                inputs.Add(MakeVkInput(vk, keyUp: true));
            }

            // Release modifiers up (reverse order)
            var modList = ParseModifiers(modifiers).ToList();
            modList.Reverse();
            foreach (var mod in modList)
            {
                inputs.Add(MakeVkInput(mod, keyUp: true));
            }

            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        });
        DesktopOverlay.MoveTo(pos.X, pos.Y);

        var label = string.IsNullOrEmpty(modifiers) ? key : $"{modifiers}+{key}";
        return $"Pressed {label}";
    }

    private static INPUT MakeUnicodeInput(char ch, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static INPUT MakeVkInput(ushort vk, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static ushort KeyToVk(string key) => key.ToLowerInvariant() switch
    {
        "enter" => 0x0D,
        "tab" => 0x09,
        "escape" or "esc" => 0x1B,
        "backspace" => 0x08,
        "delete" or "del" => 0x2E,
        "space" => 0x20,
        "arrowup" or "up" => 0x26,
        "arrowdown" or "down" => 0x28,
        "arrowleft" or "left" => 0x25,
        "arrowright" or "right" => 0x27,
        "home" => 0x24,
        "end" => 0x23,
        "pageup" => 0x21,
        "pagedown" => 0x22,
        "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73,
        "f5" => 0x74, "f6" => 0x75, "f7" => 0x76, "f8" => 0x77,
        "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
        "insert" or "ins" => 0x2D,
        "win" or "windows" or "super" => VK_LWIN,
        _ => 0
    };

    private static IEnumerable<ushort> ParseModifiers(string modifiers)
    {
        foreach (var p in modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return p.ToLowerInvariant() switch
            {
                "ctrl" or "control" => VK_CONTROL,
                "shift" => VK_SHIFT,
                "alt" => VK_MENU,
                "win" or "windows" or "super" or "meta" => VK_LWIN,
                _ => 0
            };
        }
    }

    // ─── Windows ──────────────────────────────────────────────

    /// <summary>
    /// List all visible top-level windows with title, class, and bounds.
    /// </summary>
    public static string ListWindows()
    {
        var windows = new List<string>();
        var sb = new StringBuilder(256);

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            sb.Clear();
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (string.IsNullOrEmpty(title)) return true;

            sb.Clear();
            GetClassName(hWnd, sb, sb.Capacity);
            var className = sb.ToString();

            GetWindowRect(hWnd, out var rect);
            var x = rect.Left;
            var y = rect.Top;
            var w = rect.Right - rect.Left;
            var h = rect.Bottom - rect.Top;

            var isForeground = hWnd == GetForegroundWindow();
            windows.Add($"  {(isForeground ? "▶ " : "  ")}\"{Truncate(title, 60)}\" [{className}] ({x},{y} {w}×{h}) hwnd=0x{hWnd:X}");
            return true;
        }, IntPtr.Zero);

        if (windows.Count == 0) return "No visible windows found.";
        return $"{windows.Count} visible window(s):\n{string.Join("\n", windows)}";
    }

    /// <summary>
    /// Focus a window by title (partial match, case-insensitive).
    /// Brings it to the foreground.
    /// </summary>
    public static async Task<string> FocusWindowAsync(string title)
    {
        return await Task.Run(() =>
        {
            IntPtr found = IntPtr.Zero;
            var sb = new StringBuilder(256);

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                sb.Clear();
                GetWindowText(hWnd, sb, sb.Capacity);
                var winTitle = sb.ToString();
                if (winTitle.Contains(title, StringComparison.OrdinalIgnoreCase))
                {
                    found = hWnd;
                    return false; // stop enumeration
                }
                return true;
            }, IntPtr.Zero);

            if (found == IntPtr.Zero)
                return $"Error: no visible window with title containing \"{title}\".";

            SetForegroundWindow(found);
            Thread.Sleep(200); // Let the window come to front

            GetWindowRect(found, out var rect);
            return $"Focused window: \"{Truncate(GetWindowTitle(found), 60)}\" at ({rect.Left},{rect.Top} {rect.Right - rect.Left}×{rect.Bottom - rect.Top})";
        });
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Get info about what window/control is at screen point (x, y).
    /// Returns window title, class, and bounds.
    /// </summary>
    public static string GetInfoAtPoint(int x, int y)
    {
        var pt = new POINT { X = x, Y = y };
        var hWnd = WindowFromPoint(pt);
        if (hWnd == IntPtr.Zero)
            return $"Point ({x}, {y}) — no window found.";

        var title = GetWindowTitle(hWnd);
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        var className = sb.ToString();

        GetWindowRect(hWnd, out var rect);
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;

        return $"Point ({x}, {y}) — \"{Truncate(title, 60)}\" [{className}] window at ({rect.Left},{rect.Top} {w}×{h})";
    }

    // ─── Helpers ──────────────────────────────────────────────

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
