using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Tools;

/// <summary>
/// Делает скриншот собственного окна (или всего экрана) и прикрепляет его к СВОЕМУ tool-ответу
/// (FinalizeAsync + артефакты — как в load_image): в следующем рендере модель видит экран.
/// Замыкает цикл самотестирования UI: правка → rebuild → screenshot → вижу → оцениваю → итерация.
/// </summary>
[Tool("screenshot",
    "Take a screenshot of your own app window (or the whole screen) and attach it to this tool " +
    "response so you can SEE it in the next render. Use for UI self-testing: after changing the " +
    "UI, rebuild, take a screenshot, inspect it, and iterate. Call remove_attachments when done " +
    "looking to free context.")]
public sealed class ScreenshotTool : AgentTool
{
    [ToolParameter("Region to capture: 'app' (own window, default) or 'screen' (entire primary screen).", Required = false)]
    public string Region { get; set; } = "app";

    [ToolParameter("Optional output file path (default: screenshots/screenshot_<timestamp>.png under the project root).", Required = false)]
    public string? OutputPath { get; set; }

    private string? _savedPath;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        Rectangle bounds;
        if (string.Equals(Region, "screen", StringComparison.OrdinalIgnoreCase))
        {
            var screen = Screen.PrimaryScreen;
            if (screen is null)
            {
                return "Error: no primary screen available.";
            }
            bounds = screen.Bounds;
        }
        else
        {
            var handle = Process.GetCurrentProcess().MainWindowHandle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
            {
                return "Error: the app's main window was not found (is the UI running?).";
            }
            SetForegroundWindow(handle);
            // SetForegroundWindow асинхронный: даём оконному менеджеру время поднять окно наверх,
            // иначе снимем чужое окно, стоящее поверх нашего. Task.Delay, а не Sleep: инструмент
            // выполняется на потоке UI — Sleep замораживал окно на четверть секунды.
            await Task.Delay(250, cancellationToken);
            bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        var path = string.IsNullOrWhiteSpace(OutputPath)
            ? Path.Combine(context.ProjectRoot, "screenshots", $"screenshot_{DateTime.Now:yyyyMMdd-HHmmss}.png")
            : context.ResolvePath(OutputPath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Захват + PNG-кодирование — тяжёлые синхронные GDI+-операции: уводим в пул,
        // чтобы агентные скриншоты (цикл самотестирования) не фризили интерфейс.
        await Task.Run(() =>
        {
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }
            bitmap.Save(path, ImageFormat.Png);
        }, cancellationToken);
        _savedPath = path;

        return $"Screenshot saved: {context.ToRelative(path)} ({bounds.Width}x{bounds.Height}). " +
               "It will appear in this tool response in the next render. Call remove_attachments when done.";
    }

    public override Task FinalizeAsync(ToolContext context, int messageId, CancellationToken cancellationToken)
    {
        if (_savedPath is null || messageId <= 0 || context.SessionDir is null)
        {
            return Task.CompletedTask;
        }
        try
        {
            new MessageMetaStore(context.SessionDir).AddArtifact(messageId, _savedPath);
        }
        catch
        {
            // Скриншот снят и сохранён на диске; неуспех привязки артефакта к сообщению
            // не должен ронять весь ход агента (исключение отсюда уходит в цикл без обработки).
        }
        return Task.CompletedTask;
    }
}
