using System.Reflection;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Список всех настроек приложения (AppSettings) с текущими значениями из ЖИВОЙ модели в
/// памяти и типами. Живой экземпляр — источник правды; settings.json на диске может
/// отставать (например, после правки инструментом set_setting до дебаунс-записи UI).
/// Инструмент-компаньон к set_setting: сначала get_settings, затем set_setting.
/// </summary>
[Tool("get_settings",
    "List all application settings (AppSettings) with their current in-memory values and types. " +
    "The in-memory model is the live source of truth; settings.json on disk may be stale. " +
    "Use before set_setting to see valid names and current values. " +
    "Secret properties ([SettingSecret]) are shown as <hidden — set, N chars> / <hidden — empty> — their values are never printed.")]
public sealed class GetSettingTool : AgentTool
{
    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var settings = AppSettings.Get();
        var lines = new List<string>();
        foreach (var property in typeof(AppSettings)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.CanWrite)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var value = property.GetValue(settings);
            if (value is not null && SettingDisplay.IsNestedObject(property.PropertyType))
            {
                // Вложенная группа (план, раздел 14): печатаем секцией, секреты — маркером.
                lines.Add($"- {property.Name} ({TypeName(property.PropertyType)}):");
                foreach (var sub in property.PropertyType
                             .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                             .Where(p => p.CanRead && p.CanWrite)
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    lines.Add($"  - {sub.Name} ({TypeName(sub.PropertyType)}): {SettingDisplay.Format(sub, sub.GetValue(value))}");
                }
            }
            else
            {
                lines.Add($"- {property.Name} ({TypeName(property.PropertyType)}): {SettingDisplay.Format(property, value)}");
            }
        }
        lines.Add("");
        lines.Add("Change one with set_setting(name, value). Nested groups use dotted paths, e.g. Mail.Password.");
        return Task.FromResult(string.Join("\n", lines));
    }

    private static string TypeName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.Name + (Nullable.GetUnderlyingType(type) is null ? "" : "?");
    }
}
