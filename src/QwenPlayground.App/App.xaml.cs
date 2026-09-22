using System.Windows;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Mcp;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        // Самый ранний момент: любые последующие падения (включая старт MainWindow
        // и инициализацию WebView2) уходят в CrashLog, а не в тишину.
        CrashLog.Initialize(this);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupTrace.Log("App.OnStartup: begin");
        // Страж процесса: если мы умрём мимо managed-обработчиков (нативный краш),
        // watchdog запишет смерть в общий crash-лог — картина не останется по кускам.
        WatchdogLauncher.TryStart();
        StartupTrace.Log("App.OnStartup: watchdog started");
        // Перед деплоем инструментов rebuild останавливает watchdog'а: тот держит
        // бинари launcher/ (Windows-лок), иначе сборка не смогла бы их обновить.
        SelfBuildService.PreDeployTools = WatchdogLauncher.StopWatchdog;
        SelfBuildService.PostDeployTools = WatchdogLauncher.TryStart;
        // Connect to MCP servers (non-blocking), then register their tools
        StartupTrace.Log("App.OnStartup: MCP init launched (fire-and-forget)");
        _ = McpService.InitializeAsync().ContinueWith(_ =>
        {
            // MCP ready — tools will be registered by MainViewModel via McpToolRegistrar
            StartupTrace.Log("App.OnStartup: MCP init finished");
            System.Diagnostics.Debug.WriteLine("[MCP] Init complete, tools ready for registration.");
        });
        StartupTrace.Log("App.OnStartup: done");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Маркер чистого завершения — до выхода процесса: watchdog отличает
        // «пользователь закрыл» от «умерло посреди ничего».
        WatchdogLauncher.MarkClean();
        // MCP: disconnect all servers. Сбой отключения на выходе не должен мешать
        // завершению процесса: процесс уходит в любом случае, дочерние процессы
        // заберёт ОС.
        try { McpService.ShutdownAsync().GetAwaiter().GetResult(); } catch { }
        base.OnExit(e);
    }
}
