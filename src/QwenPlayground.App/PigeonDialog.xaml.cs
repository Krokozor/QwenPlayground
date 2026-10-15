using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pigeon.Client;
using QwenPlayground.Core.Pigeon;

namespace QwenPlayground.App;

/// <summary>
/// 🕊 в тулбаре: ручное редактирование подключения ТЕКУЩЕГО чата к pigeon-серверу
/// (docs/MESSANGER.md). Идентичность = guid сессии (алиас авто = guid; модель — экстра).
/// Поток: адрес → [каналы] (публичный GET /api/channels) → выбрать канал → Подключить.
/// Статус меняется в любой момент: подключён — сразу на сервер, нет — в pigeon.json.
/// </summary>
public partial class PigeonDialog : Window
{
    private readonly string _sessionDir;

    public PigeonDialog(string sessionDir)
    {
        InitializeComponent();
        _sessionDir = sessionDir;
        IdRun.Text = Path.GetFileName(sessionDir.TrimEnd(Path.DirectorySeparatorChar));

        // Префилл из предыдущего подключения (pigeon.json).
        var prev = PigeonSession.LoadConnection(sessionDir);
        EndpointBox.Text = prev?.Endpoint ?? DefaultEndpoint();
        StatusBox.Text = prev?.Status ?? "";
        ChannelBox.Text = prev?.Channel ?? "";

        RefreshStatus();
    }

    private static string DefaultEndpoint()
    {
        var endpoint = QwenPlayground.Core.Settings.AppSettings.Get().PigeonEndpoint;
        return string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:8199" : endpoint;
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
            DisconnectButton.IsEnabled = true;
        }
        else
        {
            var err = session is { LastError: { Length: > 0 } e } ? $" Последняя ошибка: {e}" : "";
            SetStatus("не подключено" + err, Brushes.Gray, "#3a3a3a");
            DisconnectButton.IsEnabled = false;
        }
    }

    private void SetStatus(string text, Brush fg, string border)
    {
        StatusLine.Text = text;
        StatusLine.Foreground = fg;
        StatusBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
        StatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1b1e24"));
    }

    private void Fail(string message) => SetStatus(message, Brushes.IndianRed, "#4a2a2a");

    // ── Список каналов с сервера (до подключения: GET /api/channels публичный) ───────

    private async void Channels_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = EndpointBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Fail("Адрес сервера пуст — список каналов неоткуда брать.");
            return;
        }
        ChannelsButton.IsEnabled = false;
        ChannelsButton.Content = "…";
        try
        {
            // Без токена: список каналов публичный (решение владельца, 2026-10-15:
            // «ввели айпишник, порт — получили список каналов»).
            using var probe = new PigeonClient(endpoint);
            var channels = await probe.ChannelsAsync();
            ChannelBox.ItemsSource = channels.Select(c => c.Id).ToList();
            var current = ChannelBox.Text;
            if (current.Length > 0 && !channels.Any(c => c.Id == current))
            {
                StatusLine.Text = $"внимание: канал '{current}' на этом сервере не найден.";
                StatusLine.Foreground = Brushes.Goldenrod;
            }
        }
        catch (Exception ex)
        {
            Fail($"список каналов не получен: {ex.Message}");
        }
        finally
        {
            ChannelsButton.IsEnabled = true;
            ChannelsButton.Content = "каналы";
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

    // ── Connect / disconnect ─────────────────────────────────────────────────────────

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = EndpointBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Fail("Адрес сервера пуст.");
            return;
        }
        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "Подключаем…";
        try
        {
            // Статус из поля — в файл до connect: join уносит file.Status на сервер.
            PigeonSession.SaveStatusToFile(_sessionDir, StatusBox.Text.Trim());
            var error = await PigeonSession.ConnectAsync(
                _sessionDir, endpoint,
                string.IsNullOrWhiteSpace(ChannelBox.Text) ? null : ChannelBox.Text.Trim());
            RefreshStatus();
            if (error is not null && !error.StartsWith("Connected"))
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
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Подключить";
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        PigeonSession.For(_sessionDir)?.Disconnect();
        RefreshStatus();
    }
}
