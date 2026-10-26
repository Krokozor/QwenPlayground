using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Pigeon.Client;
using QwenPlayground.Core.Pigeon;

namespace QwenPlayground.App;

/// <summary>
/// 🕊 в тулбаре: ручное редактирование подключения ТЕКУЩЕГО чата к pigeon-серверу
/// (docs/MESSANGER.md). Идентичность = guid сессии (алиас авто = guid; модель — экстра).
/// UX (решение владельца, 2026-10-16): как только введён адрес — список каналов грузится
/// сам и обновляется, пока окно открыто (тик 5 с, GET /api/channels публичный); каналы —
/// список-кнопок (не дропдаун); подключить/отключить — ОДНА кнопка-переключатель.
/// Статус меняется в любой момент: подключён — сразу на сервер, нет — в pigeon.json.
/// </summary>
public partial class PigeonDialog : Window
{
    private static readonly Brush SelectedBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#26405e"));
    private static readonly Brush NormalBrush = Brushes.Transparent;

    private readonly string _sessionDir;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _endpointDebounce;
    private PigeonChannel[]? _channels;
    private string? _lastChannelsError;
    private string? _selectedChannel; // null = «без канала»
    private bool _channelsLoading;

    public PigeonDialog(string sessionDir)
    {
        InitializeComponent();
        _sessionDir = sessionDir;
        IdRun.Text = Path.GetFileName(sessionDir.TrimEnd(Path.DirectorySeparatorChar));

        // Таймеры — ДО первого присваивания Text: EndpointBox.Text ниже вызовет
        // TextChanged, а обработчик дёргает _endpointDebounce (NRE при другом порядке —
        // краш 2026-10-16).
        // Пока окно открыто — список каналов жив: периодический опрос (5 с).
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => _ = RefreshChannelsAsync();
        _refreshTimer.Start();
        Closed += (_, _) => _refreshTimer.Stop();

        // Адрес поменял содержимое — проверяем сразу (дебаунс 400 мс, чтобы не спамить
        // запросами по одному символу).
        _endpointDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _endpointDebounce.Tick += async (_, _) =>
        {
            _endpointDebounce.Stop();
            await RefreshChannelsAsync();
        };

        // Префилл из предыдущего подключения (pigeon.json).
        var prev = PigeonSession.LoadConnection(sessionDir);
        EndpointBox.Text = prev?.Endpoint ?? DefaultEndpoint(); // TextChanged → список каналов сразу
        StatusBox.Text = prev?.Status ?? "";
        _selectedChannel = prev is { Channel: { Length: > 0 } ch } ? ch : null;

        RefreshStatus();
        RebuildChannelList();
    }

    private static string DefaultEndpoint()
    {
        var endpoint = QwenPlayground.Core.Settings.AppSettings.Get().PigeonEndpoint;
        return string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:8199" : endpoint;
    }

    private void Endpoint_Changed(object sender, TextChangedEventArgs e)
    {
        _endpointDebounce.Stop();
        _endpointDebounce.Start();
    }

    // ── Статус подключения (всегда виден, цвет = состояние) ─────────────────────────

    private void RefreshStatus()
    {
        var session = PigeonSession.For(_sessionDir);
        if (session is { Connected: true })
        {
            // Live-канал (membership) приходит с первым who (~30 с); до него — канал из
            // pigeon.json (то, что запрашивалось при connect).
            var channel = session.Channel ?? PigeonSession.LoadConnection(_sessionDir)?.Channel;
            SetStatus($"подключено: {session.Endpoint} — @{session.Alias}" +
                      (channel is null ? " (не в канале)" : $" — #{channel}"),
                      Brushes.LightGreen, "#2a4a2a");
        }
        else
        {
            var err = session is { LastError: { Length: > 0 } e } ? $" Последняя ошибка: {e}" : "";
            SetStatus("не подключено" + err, Brushes.Gray, "#3a3a3a");
        }
        UpdateToggleLabel();
    }

    /// <summary>Одна кнопка-переключатель: текст и цвет = действие (решение владельца, 2026-10-16).</summary>
    private void UpdateToggleLabel()
    {
        var connected = PigeonSession.For(_sessionDir) is { Connected: true };
        ToggleButton.Content = connected ? "Отключить" : "Подключить";
        ToggleButton.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(connected ? "#4a2a2a" : "#2a4a2a"));
    }

    private void SetStatus(string text, Brush fg, string border)
    {
        StatusLine.Text = text;
        StatusLine.Foreground = fg;
        StatusBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
        StatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1b1e24"));
    }

    private void Fail(string message) => SetStatus(message, Brushes.IndianRed, "#4a2a2a");

    // ── Список каналов: кнопки, авто-обновление (до подключения GET /api/channels публичный) ──

    private async Task RefreshChannelsAsync()
    {
        var endpoint = EndpointBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _channels = null;
            _lastChannelsError = null;
            RebuildChannelList();
            return;
        }
        if (_channelsLoading)
        {
            return; // предыдущий запрос ещё летит — не гоняем параллельно
        }
        _channelsLoading = true;
        try
        {
            // Без токена: список каналов публичный (решение владельца, 2026-10-15:
            // «ввели айпишник, порт — получили список каналов»).
            using var probe = new PigeonClient(endpoint);
            _channels = await probe.ChannelsAsync();
            _lastChannelsError = null;
        }
        catch (Exception ex)
        {
            _channels = null;
            _lastChannelsError = ex.Message;
        }
        finally
        {
            _channelsLoading = false;
        }
        RebuildChannelList();
    }

    private void RebuildChannelList()
    {
        ChannelList.Children.Clear();
        ChannelList.Children.Add(MakeChannelButton(null, "— без канала —"));
        if (_channels is { Length: > 0 })
        {
            foreach (var c in _channels)
            {
                var text = $"#{(c.Name.Length > 0 ? c.Name : c.Id)}" +
                           (c.Description.Length > 0 ? $" — {c.Description}" : "");
                ChannelList.Children.Add(MakeChannelButton(c.Id, text));
            }
            // Выбранный канал пропал с сервера — показываем (не теряем выбор молча).
            if (_selectedChannel is { } sel && _channels.All(c => c.Id != sel))
            {
                ChannelList.Children.Add(MakeChannelButton(sel, $"#{sel} — не найден на сервере"));
            }
        }
        else
        {
            var hint = _channels is null
                ? (_lastChannelsError is { Length: > 0 } err
                    ? $"список каналов не получен: {err}"
                    : _channelsLoading
                        ? "загружаем список каналов…"
                        : "введите адрес сервера — список каналов загрузится автоматически")
                : "на сервере нет каналов";
            ChannelList.Children.Add(new TextBlock
            {
                Text = hint,
                Margin = new Thickness(8, 4, 8, 4),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _lastChannelsError is { Length: > 0 } ? Brushes.Goldenrod : (Brush)FindResource("DimBrush"),
            });
        }
        RestyleChannelButtons();
    }

    private Button MakeChannelButton(string? channelId, string text)
    {
        var button = new Button
        {
            Content = text,
            Tag = channelId,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 1, 0, 1),
        };
        button.Click += ChannelButton_Click;
        return button;
    }

    private void ChannelButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedChannel = (string?)((Button)sender).Tag;
        RestyleChannelButtons();
    }

    private void RestyleChannelButtons()
    {
        foreach (var child in ChannelList.Children)
        {
            if (child is Button b)
            {
                b.Background = (string?)b.Tag == _selectedChannel ? SelectedBrush : NormalBrush;
            }
        }
    }

    // ── Статус: в любой момент (подключён → сервер, нет → pigeon.json) ───────────────

    private async void SaveStatus_Click(object sender, RoutedEventArgs e)
    {
        var status = StatusBox.Text.Trim();
        var session = PigeonSession.For(_sessionDir);
        try
        {
            if (session is { Connected: true })
            {
                await session.SetStatusAsync(status);
                StatusLine.Text = "статус сохранён (на сервере).";
                StatusLine.Foreground = Brushes.LightGreen;
            }
            else
            {
                PigeonSession.SaveStatusToFile(_sessionDir, status);
                StatusLine.Text = "статус сохранён в pigeon.json (применится при подключении).";
                StatusLine.Foreground = Brushes.LightGreen;
            }
        }
        catch (Exception ex)
        {
            Fail($"статус не сохранён: {ex.Message}");
        }
    }

    // ── Подключить / отключить — одна кнопка-переключатель ───────────────────────────

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var session = PigeonSession.For(_sessionDir);
        if (session is { Connected: true })
        {
            session.Disconnect();
            RefreshStatus();
            return;
        }

        var endpoint = EndpointBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Fail("Адрес сервера пуст.");
            return;
        }
        ToggleButton.IsEnabled = false;
        ToggleButton.Content = "Подключаем…";
        try
        {
            // Статус из поля — в файл до connect: join уносит file.Status на сервер.
            PigeonSession.SaveStatusToFile(_sessionDir, StatusBox.Text.Trim());
            var error = await PigeonSession.ConnectAsync(_sessionDir, endpoint, _selectedChannel);
            if (error is not null && !error.StartsWith("Connected") && !error.StartsWith("Already"))
            {
                Fail(error);
            }
        }
        catch (Exception ex)
        {
            Fail($"Ошибка подключения: {ex.Message}");
        }
        finally
        {
            // RefreshStatus перерисует и строку статуса, и кнопку (без затирания Fail'а —
            // Fail идёт раньше, RefreshStatus при ошибке пишет «не подключено + err»).
            ToggleButton.IsEnabled = true;
            RefreshStatus();
        }
    }
}
