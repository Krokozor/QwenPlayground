using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QwenPlayground.App.Mcp;
using QwenPlayground.Core.Mcp;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.App.Views;

public partial class McpSettingsView : UserControl
{
    private readonly ObservableCollection<McpServerViewModel> _servers = new();

    public McpSettingsView()
    {
        InitializeComponent();
        ServerList.ItemsSource = _servers;
        LoadServers();
    }

    private void LoadServers()
    {
        _servers.Clear();
        var settings = AppSettings.Get();
        foreach (var s in settings.McpServers)
            _servers.Add(new McpServerViewModel(s));
        UpdateEmptyState();
    }

    private void SaveServers()
    {
        var settings = AppSettings.Get();
        settings.McpServers = _servers.Select(v => v.Config).ToList();
        AppSettings.Save();
    }

    private void UpdateEmptyState()
    {
        EmptyText.Visibility = _servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddServerButton_Click(object sender, RoutedEventArgs e)
    {
        var config = new McpServerConfig
        {
            Name = $"server_{_servers.Count + 1}",
            Enabled = true,
            Transport = "http",
            Url = "http://localhost:9876"
        };
        _servers.Add(new McpServerViewModel(config));
        SaveServers();
        UpdateEmptyState();
    }

    private async void TestServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not McpServerViewModel vm) return;

        btn.IsEnabled = false;
        vm.SetStatus("Подключение...", false);

        try
        {
            var manager = McpService.Instance;
            if (manager is null)
            {
                vm.SetStatus("MCP-сервис не инициализирован.", true);
                return;
            }

            // Disconnect existing if any, then reconnect fresh
            await manager.DisconnectAsync(vm.Config.Name);
            var client = await manager.ConnectAsync(vm.Config, CancellationToken.None);
            var tools = string.Join(", ", client.Tools.Select(t => t.Name));
            vm.SetStatus($"✓ Подключено. {client.Tools.Count} тул(ов): {tools}", false);
        }
        catch (Exception ex)
        {
            vm.SetStatus($"✕ {ex.Message}", true);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private void RemoveServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not McpServerViewModel vm) return;
        _servers.Remove(vm);
        SaveServers();
        UpdateEmptyState();
    }

    // ── ViewModel ────────────────────────────────────────────────────────────────────

    public class McpServerViewModel : INotifyPropertyChanged
    {
        public McpServerConfig Config { get; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public McpServerViewModel(McpServerConfig config) => Config = config;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ── Bound properties ──

        public string Name
        {
            get => Config.Name;
            set { Config.Name = value; Raise(); }
        }

        public string Transport
        {
            get => Config.Transport;
            set
            {
                Config.Transport = value;
                Raise();
                Raise(nameof(ShowStdio));
                Raise(nameof(ShowHttp));
            }
        }

        public bool Enabled
        {
            get => Config.Enabled;
            set { Config.Enabled = value; Save(); Raise(); }
        }

        public string Command
        {
            get => Config.Command;
            set { Config.Command = value; Save(); Raise(); }
        }

        public string Url
        {
            get => Config.Url;
            set { Config.Url = value; Save(); Raise(); }
        }

        public string ArgsText
        {
            get => string.Join(' ', Config.Args);
            set
            {
                Config.Args = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                Save();
                Raise();
            }
        }

        public Visibility ShowStdio => Config.Transport == "stdio" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ShowHttp => Config.Transport == "http" ? Visibility.Visible : Visibility.Collapsed;

        // ── Status ──

        private string _statusText = "";
        private Brush _statusBrush = Brushes.Gray;
        private Visibility _statusVisibility = Visibility.Collapsed;

        public string StatusText => _statusText;
        public Brush StatusBrush => _statusBrush;
        public Visibility StatusVisibility => _statusVisibility;

        public void SetStatus(string text, bool isError)
        {
            _statusText = text;
            _statusBrush = isError
                ? new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x60))
                : new SolidColorBrush(Color.FromRgb(0x60, 0xC0, 0x60));
            _statusVisibility = Visibility.Visible;
            Raise(nameof(StatusText));
            Raise(nameof(StatusBrush));
            Raise(nameof(StatusVisibility));
        }

        private void Save()
        {
            var settings = AppSettings.Get();
            var idx = settings.McpServers.FindIndex(c => ReferenceEquals(c, Config));
            if (idx >= 0)
            {
                settings.McpServers[idx] = Config;
                AppSettings.Save();
            }
        }
    }
}
