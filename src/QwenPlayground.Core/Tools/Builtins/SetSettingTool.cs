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

    [ToolParameter("New value as a string: a number, true/false, an enum name (XHigh/Medium/Low), or text. For complex types (lists, objects) pass a JSON string.", Required = true)]
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

            // Validate MCP server configs before applying
            if (Name.Trim().Equals("McpServers", StringComparison.OrdinalIgnoreCase) && converted is not null)
            {
                var validationError = ValidateMcpServers(converted);
                if (validationError is not null)
                    return Task.FromResult(validationError);
            }

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

        // Complex types (List<T>, custom classes, etc.) — deserialize as JSON
        if (IsComplexType(targetType))
        {
            return System.Text.Json.JsonSerializer.Deserialize(value, targetType)
                ?? throw new FormatException($"JSON deserialization to {targetType.Name} returned null.");
        }

        return Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsComplexType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return !t.IsPrimitive
            && t != typeof(string)
            && t != typeof(decimal)
            && t != typeof(DateTime)
            && t != typeof(TimeSpan)
            && t != typeof(Guid)
            && !t.IsEnum;
    }

    /// <summary>Validate MCP server configs: name format, uniqueness, transport.</summary>
    private static string? ValidateMcpServers(object servers)
    {
        if (servers is not System.Collections.IEnumerable enumerable || servers is string)
            return "Error: McpServers must be a JSON array of server objects.";

        var nameRegex = new System.Text.RegularExpressions.Regex("^[a-z][a-z0-9_]*$");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var obj in enumerable)
        {
            if (obj is not QwenPlayground.Core.Mcp.McpServerConfig server)
                return "Error: each MCP server entry must be a valid McpServerConfig object.";

            if (string.IsNullOrEmpty(server.Name))
                return "Error: MCP server with empty name. Name must match ^[a-z][a-z0-9_]*$.";
            if (!nameRegex.IsMatch(server.Name))
                return $"Error: MCP server name '{server.Name}' is invalid. Must match ^[a-z][a-z0-9_]*$ (lowercase letter first, then lowercase/digits/underscores).";
            if (!seen.Add(server.Name))
                return $"Error: duplicate MCP server name '{server.Name}'. Names must be unique.";
            if (server.Transport != "stdio" && server.Transport != "http")
                return $"Error: MCP server '{server.Name}' has invalid transport '{server.Transport}'. Must be 'stdio' or 'http'.";
            if (server.Transport == "stdio" && string.IsNullOrEmpty(server.Command))
                return $"Error: MCP server '{server.Name}' uses stdio but has no Command.";
            if (server.Transport == "http" && string.IsNullOrEmpty(server.Url))
                return $"Error: MCP server '{server.Name}' uses http but has no Url.";
        }

        return null;
    }
}
