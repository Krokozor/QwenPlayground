using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QwenPlayground.App.Browser;

/// <summary>
/// Реальное дочернее HWND внутри WPF-дерева — якорь, в который встраивается
/// агентский WebView2 (CoreWebView2Controller.ParentWindow) в интерактивном режиме
/// вкладки «Браузер».
///
/// Почему не WPF-контрол WebView2: агентский браузер создаётся вручную
/// (Environment + Controller на HWND главного окна — см. доки BrowserService.cs),
/// и контроллер можно в любой момент перепарентить на любое окно — в том числе
/// на это дочернее. Класс "Static" (встроенный класс Windows) достаточно:
/// WebView2 создаст своё собственное дочернее окно уже внутри него.
/// </summary>
public class BrowserHwndHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    protected override HandleRef BuildWindowCore(HandleRef parentId)
    {
        var hwnd = CreateWindowEx(0, "Static", "QwenPlaygroundBrowserHost",
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS, 0, 0, 0, 0,
            parentId.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Не удалось создать host-окно для интерактивного браузера");
        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Если host-окно уничтожается, пока агентский браузер в нём встраиван,
        // окно WebView2 умрёт вместе с ним — сначала возвращаем браузер «домой»
        // (off-screen на HWND главного окна).
        if (BrowserService.IsInteractive)
            BrowserService.ExitInteractiveMode();
        DestroyWindow(hwnd.Handle);
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        => DefWindowProc(hwnd, (uint)msg, wParam, lParam);
}
