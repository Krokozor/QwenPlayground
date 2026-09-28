using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace QwenPlayground.App.Views;

/// <summary>Статус сборки → цвет: success/green, failed/red, pending/gray.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value as string;
        return status?.ToLowerInvariant() switch
        {
            "success" or "ok" => Brushes.Green,
            "failed" or "error" => Brushes.Red,
            "pending" => Brushes.Gray,
            _ => Brushes.LightGray
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null/пустая строка → Collapsed, иначе → Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        return string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Любой объект null → Collapsed, иначе → Visible. Для master-detail: строковый конвертер
/// выше на объектах всегда даёт Collapsed (value as string == null) — панель деталей
/// никогда бы не показалась.
/// </summary>
public sealed class NullObjectToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → false, false → true (для IsEnabled и т.п.).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag ? !flag : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → Visible/Collapsed.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → Collapsed/Visible (инверсия BoolToVisibility).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// TODO-панель: выполненный пункт → приглушённый цвет, активный — обычный.
/// </summary>
public sealed class TodoDoneBrushConverter : IValueConverter
{
    private static readonly Brush DoneBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0));

    static TodoDoneBrushConverter()
    {
        DoneBrush.Freeze();
        ActiveBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? DoneBrush : ActiveBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// TODO-панель: выполненный пункт → перечёркнут (TextDecorations=Strikethrough),
/// активный — без декора.
/// </summary>
public sealed class TodoDoneDecorationsConverter : IValueConverter
{
    private static readonly TextDecorationCollection Strikethrough = BuildStrikethrough();

    private static TextDecorationCollection BuildStrikethrough()
    {
        var collection = new TextDecorationCollection();
        collection.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
        return collection;
    }

    // null (без декора) — легитимный результат конвертера (TextDecorations не ставится).
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Strikethrough : null!;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
