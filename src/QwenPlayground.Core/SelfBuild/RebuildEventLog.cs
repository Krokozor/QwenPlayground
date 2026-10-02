using System.IO;

namespace QwenPlayground.Core.SelfBuild;

/// <summary>
/// Лог событий ребилда — единая хронология всех участников (app, launcher, watchdog)
/// в run/launcher.log. Каждая строка — с автором: [timestamp] [source pid] message.
///
/// Ребилд — главная фича, и она должна быть неубиваемой: если что-то пошло не так
/// (два лаунчера, закрытый лаунчер, зависший handshake, потерянный watchdog),
/// полная картина «кто что и когда сделал» должна читаться из этого файла сразу,
/// без форензики по кускам. Пишут: RebuildSelfTool/SelfBuildService (app),
/// SwapService (launcher), WatchdogLauncher (app, о watchdog), Program watchdog.
/// </summary>
public static class RebuildEventLog
{
    private static readonly object Gate = new();

    public static void Log(string source, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:O}] [{source} {Environment.ProcessId}] {message}";
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(SelfBuildPaths.RunRoot, "launcher.log"), line + "\n");
            }
        }
        catch
        {
            // Лог не должен ломать процесс (ни rebuild, ни watchdog, ни launcher).
        }
    }

    public static void App(string message) => Log("app", message);
    public static void Launcher(string message) => Log("launcher", message);
    public static void Watchdog(string message) => Log("watchdog", message);
}
