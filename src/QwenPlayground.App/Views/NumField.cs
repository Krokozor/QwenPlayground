using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QwenPlayground.App.Views;

/// <summary>
/// Лёгкая валидация числового TextBox без инфраструктуры:
///  • TextChanged — текст не число (или вне [Min,Max]) → красная рамка (BorderBrush);
///    валидно → рамка возвращается к значению стиля (ClearValue).
///  • LostFocus — число вне диапазона клампится и дописывается в поле (через биндинг
///    уходит в источник). Мусор не откатывается — остаётся подсвеченным, чтобы пользователь
///    видел проблему.
/// Не трогает биндинг: работает с текстом поля. Пустое поле (seed = «случайный») не мусор.
/// Диапазоны задаются attached-свойствами в XAML: <c>local:NumField.Min / Max / IsInteger</c>.
/// </summary>
public static class NumField
{
    private static readonly Brush InvalidBrush = NewInvalidBrush();

    private static Brush NewInvalidBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xE0, 0x55, 0x55));
        brush.Freeze();
        return brush;
    }

    public static readonly DependencyProperty MinProperty =
        DependencyProperty.RegisterAttached("Min", typeof(double?), typeof(NumField),
            new PropertyMetadata(null, OnConfigChanged));

    public static readonly DependencyProperty MaxProperty =
        DependencyProperty.RegisterAttached("Max", typeof(double?), typeof(NumField),
            new PropertyMetadata(null, OnConfigChanged));

    public static readonly DependencyProperty IsIntegerProperty =
        DependencyProperty.RegisterAttached("IsInteger", typeof(bool), typeof(NumField),
            new PropertyMetadata(false, OnConfigChanged));

    public static void SetMin(DependencyObject d, double? v) => d.SetValue(MinProperty, v);
    public static double? GetMin(DependencyObject d) => (double?)d.GetValue(MinProperty);

    public static void SetMax(DependencyObject d, double? v) => d.SetValue(MaxProperty, v);
    public static double? GetMax(DependencyObject d) => (double?)d.GetValue(MaxProperty);

    public static void SetIsInteger(DependencyObject d, bool v) => d.SetValue(IsIntegerProperty, v);
    public static bool GetIsInteger(DependencyObject d) => (bool)d.GetValue(IsIntegerProperty);

    private static void OnConfigChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        tb.TextChanged -= OnTextChanged;
        tb.LostFocus -= OnLostFocus;
        tb.TextChanged += OnTextChanged;
        tb.LostFocus += OnLostFocus;
        Validate(tb);
    }

    private static bool TryParse(TextBox tb, out double value)
    {
        var raw = (tb.Text ?? string.Empty).Trim().Replace(',', '.');
        if (GetIsInteger(tb))
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            {
                value = i;
                return true;
            }
            value = 0;
            return false;
        }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static void Validate(TextBox tb)
    {
        bool invalid = false;
        if (!string.IsNullOrWhiteSpace(tb.Text))
        {
            var min = GetMin(tb);
            var max = GetMax(tb);
            invalid = !(TryParse(tb, out var v) && (min is null || v >= min) && (max is null || v <= max));
        }
        if (invalid) tb.BorderBrush = InvalidBrush;
        else tb.ClearValue(TextBox.BorderBrushProperty);
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb) Validate(tb);
    }

    private static void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var min = GetMin(tb);
        var max = GetMax(tb);
        if (min is null && max is null) return;
        if (!TryParse(tb, out var v)) return;

        var clamped = v;
        if (min is not null && clamped < min) clamped = min.Value;
        if (max is not null && clamped > max) clamped = max.Value;
        if (clamped == v) return;

        tb.Text = GetIsInteger(tb)
            ? ((int)clamped).ToString(CultureInfo.InvariantCulture)
            : clamped.ToString(CultureInfo.InvariantCulture);
    }
}
