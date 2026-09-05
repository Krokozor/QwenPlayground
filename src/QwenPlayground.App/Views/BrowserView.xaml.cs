using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QwenPlayground.App.Browser;

namespace QwenPlayground.App.Views;

public partial class BrowserView : UserControl
{
    public BrowserView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await TakeScreenshot();
    }

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
            var result = await BrowserService.NavigateAsync(url);
            StatusText.Text = result;
            await TakeScreenshot();
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
            StatusText.Text = await BrowserService.NavigateActionAsync("back", "");
            await TakeScreenshot();
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
            StatusText.Text = await BrowserService.NavigateActionAsync("forward", "");
            await TakeScreenshot();
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
            StatusText.Text = await BrowserService.NavigateActionAsync("reload", "");
            await TakeScreenshot();
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
            UrlBar.Text = url;
            StatusText.Text = $"URL: {url} | Скриншот: {path}";
        }
        catch (System.Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
        }
    }
}
