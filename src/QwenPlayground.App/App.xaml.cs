using System.Windows;
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
        // Страж процесса: если мы умрём мимо managed-обработчиков (нативный краш),
        // watchdog запишет смерть в общий crash-лог — картина не останется по кускам.
        WatchdogLauncher.TryStart();
        // Перед деплоем инструментов rebuild останавливает watchdog'а: тот держит
        // бинари launcher/ (Windows-лок), иначе сборка не смогла бы их обновить.
        SelfBuildService.PreDeployTools = WatchdogLauncher.StopWatchdog;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Маркер чистого завершения — до выхода процесса: watchdog отличает
        // «пользователь закрыл» от «умерло посреди ничего».
        WatchdogLauncher.MarkClean();
        base.OnExit(e);
    }
}
