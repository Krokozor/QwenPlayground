using System.Reflection;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Чтение и изменение настроек приложения изнутри работающего процесса.
/// AppSettings.Get() — кэшированный живой экземпляр; запись в settings.json файлом
/// не влияет на текущий процесс. Этот тул мутирует в-памяти модель и персистит.
/// Действия:
///  - list — показать все настройки с текущими значениями;
///  - set  — изменить настройку по имени (тип выводится автоматически).
/// </summary>
[Tool("set_setting",
    "Change an application setting from inside the running process. " +
    "The in-memory settings model (AppSettings) is the source of truth for the live process, " +
    "so this mutates it and persists to settings.json atomically — writing the file alone would have no effect. " +
    "Use get_settings to list valid names and current values.")]
public sealed class SetSettingTool : AgentTool
{
    [ToolParameter("AppSettings property name to change, e.g. MaxTokens, Endpoint, ReasoningEffort, Temperature. See get_settings for the full list.", Required = true)]
    public string Name { get; set; } = string.Empty;

    [ToolParameter("New value as a string: a number, true/false, an enum name (XHigh/Medium/Low), or text.", Required = true)]
    public string Value { get; set; } = string.Empty;

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var settings = AppSettings.Get();
        var prop = typeof(AppSettings).GetProperty(Name.Trim(), BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || !prop.CanWrite)
        {
            return Task.FromResult($"set_setting: property '{Name}' not found or not writable on AppSettings. Use get_settings to list valid names.");
        }

        try
        {
            var converted = ConvertValue(prop.PropertyType, Value.Trim());
            var old = prop.GetValue(settings);
            prop.SetValue(settings, converted);
            AppSettings.Save();
            return Task.FromResult($"set_setting: {Name} = {old} → {converted}. Persisted to settings.json.");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"set_setting: failed to set '{Name}' to '{Value}': {ex.Message}");
        }
    }

    private static object? ConvertValue(Type targetType, string value)
    {
        if (targetType == typeof(string)) return value;
        if (targetType == typeof(int)) return int.Parse(value);
        if (targetType == typeof(double)) return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        if (targetType == typeof(bool)) return bool.Parse(value);
        if (targetType.IsEnum) return Enum.Parse(targetType, value, ignoreCase: true);
        return Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
    }
}
