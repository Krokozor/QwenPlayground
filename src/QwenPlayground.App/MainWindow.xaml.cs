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
            if (vm is null)
            {
                return;
            }
            // Сначала разговор (хотя бы сообщение, если ход ещё шёл), затем сервисы
            // (heartbeat стоп, синхронный flush настроек) в порядке LIFO.
            try
            {
                vm.SaveCurrent();
                vm.Shutdown();
            }
            catch (Exception exception)
            {
                // Сбой на выходе не должен остаться без следа: процесс вот-вот умрёт,
                // а диалог Dispatcher-обработчика в момент закрытия — ненадёжный канал.
                // Запись в общий crash-лог — состояние (в т.ч. несохранённая сессия)
                // станет объяснимо при следующем запуске.
                CrashLog.LogCrash("Shutdown", "сбой сохранения состояния при закрытии", exception);
            }
        };
        Loaded += (_, _) =>
        {
            SelfBuildService.WriteHandshake();

            // Attach browser service to the hidden WebView2 (always in tree)
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
                    // Таймер одноразовый: без catch pending-цепь молча бы не возобновилась,
                    // и ход завис бы «в полете» без единой записи.
                    CrashLog.LogCrash("ResumePendingChain", "pending-цепь не возобновлена — ход может не продолжиться", exception);
                }
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
