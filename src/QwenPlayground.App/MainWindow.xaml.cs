using System.Windows;
using QwenPlayground.App.Browser;
using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkWindowFrame.Apply(this);
        Closing += (_, _) =>
        {
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
            SelfBuildService.WriteHandshake();
            BrowserService.Attach(AgentBrowser);

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    (DataContext as MainViewModel)?.ResumePendingChain();
                }
                catch (Exception exception)
                {
                    CrashLog.LogCrash("ResumePendingChain", "pending-цепь не возобновлена", exception);
                }
            };
            timer.Start();
            if (ViewModels.StateBlockBuilder.LastBuild() is { } last)
            {
                Title = $"QwenPlayground [{last.Id}]";
            }
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
