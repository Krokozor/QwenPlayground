using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QwenPlayground.App;

/// <summary>
/// Тёмная неклиентская область окна (заголовок + его цвет) через DWM.
/// Без этого WPF-окно получает светлую рамку от ОС, которая ломает тёмную тему.
/// Работает на Windows 10 2004+ (build 19041+) и Windows 11.
/// </summary>
internal static class DarkWindowFrame
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35; // undocumented, Win10 2004+

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    /// <summary>Включить тёмный заголовок и подогнать его цвет под тему. Вызывать после SourceInitialized.</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        int enabled = 1;
        // E_NOTIMPL / E_INVALIDARG на старых ОС — молча игнорируем, окно просто останется светлым.
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));

        // Дефолтный тёмный заголовок Win10 — #2b2b2b, цвет вне нашей палитры.
        // Подгоняем под InputBrush (#2d2d30): чуть светлее дефолта, в тон полям ввода.
        int captionColor = 0x002D2D30; // COLORREF: 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
    }
}
