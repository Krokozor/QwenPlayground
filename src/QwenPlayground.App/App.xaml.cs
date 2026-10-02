using System.IO;
using System.Windows;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Mcp;
using QwenPlayground.Core.Runtime;
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
        // Хронология ребилда: приложение записывает свои события в run/launcher.log
        // (единый лог app+launcher+watchdog, каждая строка с автором).
        var buildId = System.IO.Path.GetFileName(System.AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        // Pointer рядом: если запущена не та версия (ручной старт старой, pointer уже
        // переключён) — несоответствие видно на одной строке, без сопоставления.
        var pointerPath = Path.Combine(SelfBuildPaths.RunRoot, "current.txt");
        var pointer = File.Exists(pointerPath) ? File.ReadAllText(pointerPath).Trim() : "none";
        RebuildEventLog.App($"app started (pid {Environment.ProcessId}, build {(SelfBuildPaths.TryGetDeployedRunRoot(out _) ? buildId : "dev")}, pointer={pointer})");
        // Инвариант UI-потока: мутаторы ядра (ChatLog, FSM, MemoryStore, метки слотов)
        // проверяют, что исполняются на dispatcher-потоке. Нарушение — events-лог + throw
        // (degradation must be loud). Core не знает WPF — шнуровка здесь, в App.
        UiThreadPolicy.IsUiThread = () => Dispatcher.CheckAccess();
        // Детектор зависания старта: если UI-поток не отвечает 10+ с до рукопожатия,
        // пишет маркер HANG + хвост трейса (баг «клиент не запускается после ребилда»).
        StartupHangGuard.Start(Dispatcher);
        // UI-beat до Loaded: 1-с heartbeat в старт-трейс на всё окно старта — точка
        // зависания видна с точностью до секунды (после Loaded работает свой alive-таймер).
        var beat = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var beatStart = DateTime.Now;
        beat.Tick += (_, _) =>
        {
            StartupTrace.Log($"UI beat (+{(DateTime.Now - beatStart).TotalSeconds:F0}s, pre-Loaded)");
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "ok.marker"))
                || DateTime.Now - beatStart > TimeSpan.FromSeconds(120))
            {
                beat.Stop();
            }
        };
        beat.Start();
        // Страж процесса: если мы умрём мимо managed-обработчиков (нативный краш),
        // watchdog запишет смерть в общий crash-лог — картина не останется по кускам.
        WatchdogLauncher.TryStart();
        StartupTrace.Log("App.OnStartup: watchdog started");
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
        RebuildEventLog.App($"app OnExit (pid {Environment.ProcessId}) — clean shutdown, watchdog marker written");
        // MCP: disconnect all servers. Сбой отключения на выходе не должен мешать
        // завершению процесса: процесс уходит в любом случае, дочерние процессы
        // заберёт ОС.
        try { McpService.ShutdownAsync().GetAwaiter().GetResult(); } catch { }
        base.OnExit(e);
    }
}
