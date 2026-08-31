using System.IO;
using System.Windows;
using System.Windows.Controls;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Launcher;

public partial class MainWindow : Window
{
    private LauncherConfig _config = null!;

    public MainWindow()
    {
        InitializeComponent();
        _config = LauncherConfig.Load();
        RefreshStatus();
        BuildToolsPanel();
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

        // Git status
        if (GitService.IsGitRepo(SelfBuildPaths.WorkspaceRoot))
        {
            _ = UpdateGitStatusAsync();
        }
        else
        {
            GitStatusText.Text = "git: не инициализирован";
        }

        var logPath = Path.Combine(SelfBuildPaths.RunRoot, "launcher.log");
        if (File.Exists(logPath))
        {
            var lines = File.ReadAllLines(logPath);
            LogBox.Text = string.Join(Environment.NewLine, lines.TakeLast(12));
            LogBox.ScrollToEnd();
        }
    }

    private async Task UpdateGitStatusAsync()
    {
        try
        {
            var head = await GitService.GetHeadCommitAsync();
            var hasUpdates = await GitService.HasRemoteUpdatesAsync();
            GitStatusText.Text = head is null
                ? "git: ?"
                : $"git: {head}" + (hasUpdates ? " (есть обновления)" : "");
        }
        catch
        {
            GitStatusText.Text = "git: ошибка";
        }
    }

    private void BuildToolsPanel()
    {
        ToolsPanel.Children.Clear();
        foreach (var (name, tool) in _config.Tools)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

            var statusText = new TextBlock
            {
                Text = ToolManager.IsInstalled(tool) ? $"{name}: установлен" : $"{name}: не установлен",
                Width = 180,
                Foreground = ToolManager.IsInstalled(tool) ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red
            };
            panel.Children.Add(statusText);

            if (ToolManager.IsInstalled(tool))
            {
                var uninstallBtn = new Button { Content = "Удалить", Padding = new Thickness(8, 2, 8, 2) };
                uninstallBtn.Click += async (_, _) => await UninstallToolAsync(name, tool);
                panel.Children.Add(uninstallBtn);
            }
            else
            {
                var installBtn = new Button { Content = "Установить", Padding = new Thickness(8, 2, 8, 2) };
                installBtn.Click += async (_, _) => await InstallToolAsync(name, tool);
                panel.Children.Add(installBtn);
            }

            ToolsPanel.Children.Add(panel);
        }
    }

    private async Task InstallToolAsync(string name, ToolConfig tool)
    {
        SetBusy(true);
        StatusText.Text = $"Установка {name}...";
        var progress = new Progress<string>(msg => StatusText.Text = msg);
        var (success, message) = await ToolManager.InstallAsync(tool, progress);
        StatusText.Text = success ? $"{name}: {message}" : $"{name}: ОШИБКА — {message}";
        SetBusy(false);
        BuildToolsPanel();
        RefreshStatus();
    }

    private async Task UninstallToolAsync(string name, ToolConfig tool)
    {
        SetBusy(true);
        StatusText.Text = $"Удаление {name}...";
        ToolManager.Uninstall(tool);
        StatusText.Text = $"{name}: удалён";
        SetBusy(false);
        BuildToolsPanel();
        RefreshStatus();
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

    private async void PullButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        StatusText.Text = "git pull...";
        try
        {
            var (exitCode, output) = await GitService.PullAsync();
            StatusText.Text = exitCode == 0
                ? $"pull OK: {output.Split('\n').FirstOrDefault()?.Trim() ?? ""}"
                : $"pull ОШИБКА:\n{output}";
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

    private async void PullRebuildButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        StatusText.Text = "git pull + сборка + перезапуск…";
        try
        {
            // 1. Pull
            var (pullExit, pullOutput) = await GitService.PullAsync();
            if (pullExit != 0)
            {
                StatusText.Text = $"pull ОШИБКА:\n{pullOutput}";
                return;
            }
            StatusText.Text = "pull OK, сборка…";

            // 2. Rebuild
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
        PullButton.IsEnabled = !busy;
        PullRebuildButton.IsEnabled = !busy;
    }
}
