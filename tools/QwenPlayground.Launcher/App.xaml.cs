using System.Windows;

namespace QwenPlayground.Launcher;

/// <summary>
/// Точка входа лаунчера.
/// - С аргументами `&lt;pid&gt; [buildId]` — headless self-rebuild, вызывается самим
///   приложением (RestartInto): ждём старый процесс, меняем версию, окна не показываем.
/// - Без аргументов — минимальный GUI: запуск активной версии и пересборка из исходников.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 1 && int.TryParse(e.Args[0], out var pid))
        {
            var buildId = e.Args.Length > 1 ? e.Args[1] : null;
            Shutdown(SwapService.RunSwapped(pid, buildId));
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
