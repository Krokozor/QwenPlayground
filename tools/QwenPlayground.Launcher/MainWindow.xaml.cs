using System.IO;
using System.Windows;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Launcher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var versionId = SwapService.CurrentVersionId();
        VersionText.Text = versionId is null
            ? "активной версии нет (нет current.txt и current/)"
            : $"активная версия: {versionId}";

        var last = BuildJournal.Load(SelfBuildPaths.RunRoot).LastOrDefault();
        BuildStatusText.Text = last is null
            ? string.Empty
            : $"последняя сборка: {last.Id} — {last.Status}" + (last.FailureReason is null ? string.Empty : $" ({last.FailureReason})");

        var logPath = Path.Combine(SelfBuildPaths.RunRoot, "launcher.log");
        if (File.Exists(logPath))
        {
            var lines = File.ReadAllLines(logPath);
            LogBox.Text = string.Join(Environment.NewLine, lines.TakeLast(12));
            LogBox.ScrollToEnd();
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var pid = SwapService.StartCurrent();
            StatusText.Text = $"запущено (pid {pid}).";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
        }
    }

    private async void RebuildButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        StatusText.Text = "сборка + тест-гейт, затем перезапуск…";
        try
        {
            var (exitCode, message) = await Task.Run(() =>
                SwapService.RebuildAndStartAsync(CancellationToken.None));
            StatusText.Text = exitCode == 0
                ? message
                : "ОШИБКА\n" + message;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"ошибка: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
        }
    }

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy;
        RebuildButton.IsEnabled = !busy;
    }
}
