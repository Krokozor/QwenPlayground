using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QwenPlayground.App.Views;

/// <summary>
/// bool (SubagentChat.IsGenerating) → подпись статуса в хедере встроенного чата
/// субагента: «субагент: работает…» / «субагент: завершён».
/// </summary>
public sealed class SubagentStatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "субагент: работает…" : "субагент: завершён";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// double (высота окна) → половина: MaxHeight встроенного чата субагента (50% окна).
/// </summary>
public sealed class HalfHeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? d * 0.5 : double.NaN;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Видимость строки ввода встроенного чата (MultiBinding: [Embedded, ShowSubagentInput]):
/// в оконном режиме — всегда Visible; во встроенном — Visible только когда «✎» в хедере
/// раскрыл ввод (по дефолту скрыт для компактности).
/// </summary>
public sealed class EmbeddedInputVisibilityConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) {
        var embedded = values is { Length: >= 1 } && values[0] is true;
        var showInput = values is { Length: >= 2 } && values[1] is true;
        return !embedded || showInput ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
