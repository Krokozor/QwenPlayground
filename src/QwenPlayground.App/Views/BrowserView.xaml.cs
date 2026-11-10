using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QwenPlayground.App.Browser;

namespace QwenPlayground.App.Views;

public partial class BrowserView : UserControl
{
    private bool _toggling;

    public BrowserView()
    {
        InitializeComponent();

        Loaded += async (_, _) => await TakeScreenshot();

        // Вкладка скрыта (переключились на другую) — встроенный браузер спрятался бы
        // вместе с HwndHost, и движок WebView2 завис бы: возвращаем браузер агенту.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false && BrowserService.IsInteractive)
                ExitInteractive();
        };
        Unloaded += (_, _) =>
        {
            if (BrowserService.IsInteractive)
                ExitInteractive();
        };

        // Живой URL в адресной строке (навигация агента или владельца).
        // Не затираем строку, пока пользователь печатает в ней.
        BrowserService.UrlChanged += url =>
        {
            void Set() { if (!UrlBar.IsKeyboardFocusWithin) UrlBar.Text = url; }
            if (Dispatcher.CheckAccess()) Set();
            else Dispatcher.Invoke(Set);
        };
    }

    // ─── Interactive mode ─────────────────────────────────────

    private async void ToggleMode_Click(object sender, RoutedEventArgs e)
    {
        if (_toggling) return;
        _toggling = true;
        try
        {
            if (BrowserService.IsInteractive)
                ExitInteractive();
            else
                await EnterInteractiveAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
            ModeButton.IsEnabled = true;
        }
        finally
        {
            _toggling = false;
        }
    }

    private async Task EnterInteractiveAsync()
    {
        ModeButton.IsEnabled = false;
        StatusText.Text = "Встраиваю живой браузер...";
        ScreenshotBorder.Visibility = Visibility.Collapsed;
        Host.Visibility = Visibility.Visible;
        UpdateLayout(); // Host должен получить реальный размер до репарентинга контроллера
        var (w, h) = HostPhysicalSize();
        await BrowserService.EnterInteractiveModeAsync(Host.Handle, w, h);
        ModeButton.IsEnabled = true; // защита от двойного клика — флаг _toggling, а не IsEnabled
        ModeButton.Content = "↩ Вернуть агенту";
        ScreenshotButton.IsEnabled = false;
        StatusText.Text = "Живой браузер: вы управляете сами (клик, печать, навигация). " +
                          "Агент в это время только смотрит, не трогает. " +
                          "'↩ Вернуть агенту' — отдать управление обратно.";
    }

    private void ExitInteractive()
    {
        BrowserService.ExitInteractiveMode();
        Host.Visibility = Visibility.Collapsed;
        ScreenshotBorder.Visibility = Visibility.Visible;
        ModeButton.Content = "✋ Живой браузер";
        ModeButton.IsEnabled = true;
        ScreenshotButton.IsEnabled = true;
        StatusText.Text = "Браузер возвращён агенту (headless, 1280x800).";
        _ = TakeScreenshot();
    }

    /// <summary>Размер Host в физических пикселях (WebView2 работает в них, а WPF — в DIP).</summary>
    private (int Width, int Height) HostPhysicalSize()
    {
        var dpi = VisualTreeHelper.GetDpi(Host);
        return (
            Math.Max(1, (int)Math.Round(Host.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(Host.ActualHeight * dpi.DpiScaleY)));
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!BrowserService.IsInteractive) return;
        var dpi = VisualTreeHelper.GetDpi(Host);
        BrowserService.UpdateInteractiveBounds(
            Math.Max(1, (int)Math.Round(e.NewSize.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(e.NewSize.Height * dpi.DpiScaleY)));
    }

    // ─── Toolbar (owner-пути: работают и в скриншот-режиме, и в интерактивном) ───

    private async void UrlBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        try
        {
            var url = UrlBar.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }
            StatusText.Text = "Навигация...";
            var result = await BrowserService.OwnerNavigateAsync(url);
            StatusText.Text = result;
            if (!BrowserService.IsInteractive) await TakeScreenshot();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
        }
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = await BrowserService.OwnerNavigateActionAsync("back", "");
            if (!BrowserService.IsInteractive) await TakeScreenshot();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
        }
    }

    private async void Forward_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = await BrowserService.OwnerNavigateActionAsync("forward", "");
            if (!BrowserService.IsInteractive) await TakeScreenshot();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = await BrowserService.OwnerNavigateActionAsync("reload", "");
            if (!BrowserService.IsInteractive) await TakeScreenshot();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
        }
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e) => await TakeScreenshot();

    private async Task TakeScreenshot()
    {
        try
        {
            var path = await BrowserService.ScreenshotAsync();
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            ScreenshotImage.Source = bmp;
            var url = await BrowserService.GetCurrentUrlAsync();
            if (!UrlBar.IsKeyboardFocusWithin) UrlBar.Text = url;
            StatusText.Text = $"URL: {url} | Скриншот: {path}";
        }
        catch (System.Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
        }
    }
}
