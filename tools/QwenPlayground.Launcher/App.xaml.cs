using System.Windows;

namespace QwenPlayground.Launcher;

/// <summary>
/// Точка входа лаунчера.
/// - С аргументами `&lt;pid&gt; [buildId]` — headless self-rebuild, вызывается самим
///   приложением (RestartInto): ждём старый процесс, меняем версию, окна не показываем.
/// - Без аргументов — минимальный GUI: запуск активной версии и пересборка из исходников.
/// Оба режима под CrashLog-обработчиками (LauncherCrash): смерть лаунчера пишется
/// в logs/launcher-crash-*.log, а не растворяется в тишине.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 1 && int.TryParse(e.Args[0], out var pid))
        {
            LauncherCrash.InitializeHeadlessHandlers();
            base.OnStartup(e);
            var buildId = e.Args.Length > 1 ? e.Args[1] : null;
            Shutdown(SwapService.RunSwapped(pid, buildId));
            return;
        }

        LauncherCrash.InitializeGuiHandlers(this);
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
