using System.Windows;
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
            if (vm is null)
            {
                return;
            }
            // Сначала разговор (хотя бы сообщение, если ход ещё шёл), затем сервисы
            // (heartbeat стоп, синхронный flush настроек) в порядке LIFO.
            vm.SaveCurrent();
            vm.Shutdown();
        };
        Loaded += (_, _) =>
        {
            SelfBuildService.WriteHandshake();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                (DataContext as MainViewModel)?.ResumePendingChain();
            };
            timer.Start();
            if (ViewModels.StateBlockBuilder.LastBuild() is { } last)
            {
                Title = $"QwenPlayground [{last.Id}]";
            }
        };
    }

    /// <summary>Отмена хода из списка (кнопка видна только у активных).</summary>
    private void TurnCancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TurnPanelItem item &&
            DataContext is MainViewModel { TurnsPanel: { } panel })
        {
            panel.Cancel(item);
        }
    }
}
