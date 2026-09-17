using System.Windows;
using QwenPlayground.App.Browser;
using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.App;

public partial class MainWindow : Window
{
    private DateTime _loadedAt;

    public MainWindow()
    {
        StartupTrace.Log("MainWindow ctor: begin (InitializeComponent создаёт MainViewModel)");
        InitializeComponent();
        StartupTrace.Log("MainWindow ctor: InitializeComponent done");
        SourceInitialized += (_, _) => DarkWindowFrame.Apply(this);
        Closing += (_, _) =>
        {
            StartupTrace.Log("MainWindow Closing: begin");
            var vm = DataContext as MainViewModel;
            if (vm is null) return;
            try
            {
                vm.SaveCurrent();
                vm.Shutdown();
            }
            catch (Exception exception)
            {
                CrashLog.LogCrash("Shutdown", "сбой сохранения состояния при закрытии", exception);
            }
        };
        Loaded += (_, _) =>
        {
            _loadedAt = DateTime.Now;
            StartupTrace.Log("MainWindow Loaded: begin");
            SelfBuildService.WriteHandshake();
            StartupTrace.Log("MainWindow Loaded: handshake written");
            BrowserService.Attach(AgentBrowser);
            StartupTrace.Log("MainWindow Loaded: browser attached");

            // Живость UI-потока: если строки прекратились, а процесс жив — UI-поток заблокирован.
            var aliveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            aliveTimer.Tick += (_, _) =>
                StartupTrace.Log($"UI alive (+{(DateTime.Now - _loadedAt).TotalSeconds:F0}s)");
            aliveTimer.Start();

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                StartupTrace.Log("MainWindow: ResumePendingChain begin");
                try
                {
                    (DataContext as MainViewModel)?.ResumePendingChain();
                }
                catch (Exception exception)
                {
                    CrashLog.LogCrash("ResumePendingChain", "pending-цепь не возобновлена", exception);
                }
                StartupTrace.Log("MainWindow: ResumePendingChain end");
            };
            timer.Start();
            if (ViewModels.StateBlockBuilder.LastBuild() is { } last)
            {
                Title = $"QwenPlayground [{last.Id}]";
            }
            StartupTrace.Log("MainWindow Loaded: done");
        };
    }

    private void TurnCancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TurnPanelItem item &&
            DataContext is MainViewModel { TurnsPanel: { } panel })
        {
            panel.Cancel(item);
        }
    }
}
