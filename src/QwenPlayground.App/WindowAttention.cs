using System.Runtime.InteropServices;

namespace QwenPlayground.App;

/// <summary>
/// Привлечение внимания пользователя к окну: фокус + мигание в панели задач.
/// FlashWindowEx — то же «мигание», что было у старого диалога подтверждения
/// (винда подсвечивала его в панели задач): работает даже когда Windows не даёт
/// фоновому процессу украсть первый план. Используется, когда агент ждёт решения
/// (карточка подтверждения) и окно может быть не на переднем плане.
/// </summary>
public static class WindowAttention
{
    /// <summary>Показать/активировать окно и (если оно не на переднем плане) замигать его в панели задач.</summary>
    public static void Focus(System.Windows.Window window)
    {
        if (window is not { IsLoaded: true })
        {
            return;
        }
        // Handle может быть ещё не создан — WindowInteropHelper создаёт его при необходимости.
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            window.Show();
            window.Activate();
            if (GetForegroundWindow() != handle)
            {
                BlinkTaskbar(handle);
            }
        }));
    }

    private static void BlinkTaskbar(IntPtr handle)
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new HandleRef(null, handle),
            dwFlags = FLASHW_ALL | FLASHW_TIMED, // иконка + панель задач, 5 раз
            uCount = 5,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }

    private const uint FLASHW_CAPTION = 1;
    private const uint FLASHW_TRAY = 2;
    private const uint FLASHW_ALL = FLASHW_CAPTION | FLASHW_TRAY;
    private const uint FLASHW_TIMER = 4;
    private const uint FLASHW_TIMERNOFPS = 8;
    private const uint FLASHW_TIMED = FLASHW_TIMER | FLASHW_TIMERNOFPS;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public HandleRef hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
