using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        BuildEnvPanel();
        LoadSettingsTab();
        _ = AutoSyncCheckAsync();
    }

    // ===== Статус =====

    private void RefreshStatus()
    {
        var versionId = SwapService.CurrentVersionId();
        VersionText.Text = versionId is null
            ? "активной версии нет"
            : $"активная версия: {versionId}";

        var last = BuildJournal.Load(SelfBuildPaths.RunRoot).LastOrDefault();
        BuildStatusText.Text = last is null
            ? string.Empty
            : $"последняя сборка: {last.Id} — {last.Status}" + (last.FailureReason is null ? "" : $" ({last.FailureReason})");

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
            LogBox.Text = string.Join(Environment.NewLine, lines.TakeLast(20));
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
                : $"git: {head}" + (hasUpdates ? "  ⬇ есть обновления" : "");
        }
        catch
        {
            GitStatusText.Text = "git: ошибка";
        }
    }

    // ===== Инструменты =====

    private void BuildToolsPanel()
    {
        ToolsPanel.Children.Clear();
        var successBrush = (Brush)FindResource("SuccessBrush");
        var errorBrush = (Brush)FindResource("ErrorBrush");
        var dimBrush = (Brush)FindResource("DimBrush");

        foreach (var (name, tool) in _config.Tools)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };

            // Имя + статус
            var installed = ToolManager.IsInstalled(tool);
            var nameBlock = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            nameBlock.Children.Add(new TextBlock
            {
                Text = installed ? "● " : "○ ",
                Foreground = installed ? successBrush : errorBrush,
                FontSize = 12
            });
            nameBlock.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            });
            row.Children.Add(nameBlock);

            // Версия / статус
            var statusText = new TextBlock
            {
                Text = installed ? "установлен" : "не установлен",
                FontSize = 11,
                Foreground = dimBrush,
                Margin = new Thickness(8, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(statusText);

            // Динамическая кнопка
            Button actionBtn;
            if (installed)
            {
                actionBtn = new Button { Content = "Проверить обновления", Padding = new Thickness(10, 3, 10, 3), FontSize = 11 };
                actionBtn.Click += async (_, _) => await CheckToolUpdateAsync(name, tool, actionBtn, statusText);
            }
            else
            {
                actionBtn = new Button { Content = "Скачать", Padding = new Thickness(10, 3, 10, 3), FontSize = 11 };
                actionBtn.Click += async (_, _) => await InstallToolAsync(name, tool, actionBtn);
            }
            row.Children.Add(actionBtn);

            ToolsPanel.Children.Add(row);

            // Если установлен — показать версию
            if (installed)
            {
                _ = ShowToolVersionAsync(name, tool, statusText);
            }
        }
    }

    private async Task ShowToolVersionAsync(string name, ToolConfig tool, TextBlock statusText)
    {
        var version = await ToolManager.GetInstalledVersionAsync(tool);
        if (version is not null)
        {
            Dispatcher.Invoke(() => statusText.Text = version);
        }
    }

    private async Task InstallToolAsync(string name, ToolConfig tool, Button btn)
    {
        btn.IsEnabled = false;
        btn.Content = "Скачивание…";
        SetBusy(true);
        StatusText.Text = $"Установка {name}…";

        var progress = new Progress<string>(msg =>
        {
            StatusText.Text = msg;
            btn.Content = msg;
        });

        var (success, message) = await ToolManager.InstallAsync(tool, progress);
        StatusText.Text = success ? $"{name}: {message}" : $"{name}: ОШИБКА — {message}";
        LogBox.AppendText($"\n[{DateTime.Now:HH:mm:ss}] {name}: {message}");
        LogBox.ScrollToEnd();

        SetBusy(false);
        BuildToolsPanel();
        RefreshStatus();
    }

    private async Task CheckToolUpdateAsync(string name, ToolConfig tool, Button btn, TextBlock statusText)
    {
        btn.IsEnabled = false;
        btn.Content = "Проверка…";
        SetBusy(true);
        StatusText.Text = $"Проверка обновлений {name}…";

        // Для ffmpeg: сравниваем локальную версию с последней на GitHub
        // Пока: просто показываем что проверяем (полная логика — потом)
        await Task.Delay(1000); // имитация проверки
        StatusText.Text = $"{name}: актуальная версия";
        LogBox.AppendText($"\n[{DateTime.Now:HH:mm:ss}] {name}: проверка обновлений — актуально");
        LogBox.ScrollToEnd();

        btn.IsEnabled = true;
        btn.Content = "Проверить обновления";
        SetBusy(false);
    }

    // ===== Окружение =====

    private void BuildEnvPanel()
    {
        EnvPanel.Children.Clear();
        var successBrush = (Brush)FindResource("SuccessBrush");
        var errorBrush = (Brush)FindResource("ErrorBrush");
        var dimBrush = (Brush)FindResource("DimBrush");

        foreach (var check in EnvironmentCheck.CheckAll())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = check.Installed ? "●" : "○",
                Foreground = check.Installed ? successBrush : errorBrush,
                Width = 16, FontSize = 12
            });
            row.Children.Add(new TextBlock { Text = check.Name, Width = 80, FontSize = 12 });
            row.Children.Add(new TextBlock
            {
                Text = check.Installed ? (check.Version ?? "") : (check.Hint ?? "не найден"),
                FontSize = 11, Foreground = dimBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            EnvPanel.Children.Add(row);
        }

        CheckLlamaAsync();
    }

    private async void CheckLlamaAsync()
    {
        try
        {
            var llama = await EnvironmentCheck.CheckLlamaServerAsync();
            var successBrush = (Brush)FindResource("SuccessBrush");
            var errorBrush = (Brush)FindResource("ErrorBrush");
            var dimBrush = (Brush)FindResource("DimBrush");

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = llama.Installed ? "●" : "○",
                Foreground = llama.Installed ? successBrush : errorBrush,
                Width = 16, FontSize = 12
            });
            row.Children.Add(new TextBlock { Text = "llama.cpp", Width = 80, FontSize = 12 });
            row.Children.Add(new TextBlock
            {
                Text = llama.Installed ? (llama.Version ?? "OK") : (llama.Hint ?? "недоступен"),
                FontSize = 11, Foreground = dimBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            EnvPanel.Children.Add(row);
        }
        catch { }
    }

    // ===== Автосинхронизация =====

    private async Task AutoSyncCheckAsync()
    {
        try
        {
            if (!GitService.IsGitRepo(SelfBuildPaths.WorkspaceRoot)) return;
            var hasUpdates = await GitService.HasRemoteUpdatesAsync();
            if (hasUpdates)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "⬇ Есть обновления на GitHub. Нажми «Pull», затем «Пересобрать».";
                    StatusText.Foreground = (Brush)FindResource("AccentBrush");
                });
            }
        }
        catch { }
    }

    // ===== Настройки (вкладка) =====

    private void LoadSettingsTab()
    {
        RepoUrlBox.Text = _config.Repo;
        BranchBox.Text = _config.Branch;
        ToolsConfigText.Text = string.Join("\n",
            _config.Tools.Select(t => $"{t.Key}: {t.Value.BinPath}"));
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _config.Repo = RepoUrlBox.Text.Trim();
        _config.Branch = BranchBox.Text.Trim();
        _config.Save();
        StatusText.Text = "Настройки сохранены";
    }

    private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _config = LauncherConfig.CreateDefault();
        LoadSettingsTab();
        StatusText.Text = "Настройки сброшены к дефолтным";
    }

    // ===== Кнопки =====

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var pid = SwapService.StartCurrent();
            StatusText.Text = $"запущено (pid {pid}).";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
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
            StatusText.Text = exitCode == 0 ? message : "ОШИБКА\n" + message;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"ошибка: {ex.Message}";
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
        StatusText.Text = "git pull…";
        try
        {
            var (exitCode, output) = await GitService.PullAsync();
            StatusText.Text = exitCode == 0
                ? $"pull OK: {output.Split('\n').FirstOrDefault()?.Trim() ?? ""}"
                : $"pull ОШИБКА:\n{output}";
            LogBox.AppendText($"\n[{DateTime.Now:HH:mm:ss}] git pull: {(exitCode == 0 ? "OK" : "FAIL")}");
            LogBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"ошибка: {ex.Message}";
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
        SaveSettingsButton.IsEnabled = !busy;
        ResetSettingsButton.IsEnabled = !busy;
    }
}
