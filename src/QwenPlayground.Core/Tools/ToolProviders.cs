using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;

namespace QwenPlayground.Core.Tools;

/// <summary>
/// Зарегистрированный инструмент: определение для промпта (имя/описание/схема) + исполнитель.
/// Определение может происходить откуда угодно — рефлексия по [Tool]-классам (встроенные),
/// MCP-сервер (динамическая схема), плагин. Исполнитель получает уже сырые аргументы модели.
/// </summary>
public sealed class ToolEntry
{
    public required ToolDefinition Definition { get; init; }

    /// <summary>
    /// Выполнение. Возвращает текст ответа tool и опционально экземпляр AgentTool —
    /// если нужен этап финализации (<see cref="AgentTool.FinalizeAsync"/>, вызывается
    /// после добавления tool-сообщения в разговор с известным ID). Динамическим
    /// инструментам (MCP) финализация не нужна — Tool = null.
    /// </summary>
    public required Func<JsonObject, ToolContext, CancellationToken, Task<ToolExecutionResult>> Execute { get; init; }
}

/// <summary>Источник инструментов для реестра (встроенная рефлексия, MCP, плагины).</summary>
public interface IToolProvider
{
    IEnumerable<ToolEntry> Discover();
}

/// <summary>
/// Классический источник: классы-наследники <see cref="AgentTool"/> с атрибутом [Tool]
/// в указанных сборках; параметры объявляются свойствами с [ToolParameter]. JSON-схема
/// для промпта и биндинг аргументов выводятся рефлексией из атрибутов —
/// новый встроенный инструмент = новый класс, регистрация не нужна.
/// </summary>
public sealed class ReflectionToolProvider : IToolProvider
{
    private readonly Assembly[] _assemblies;

    public ReflectionToolProvider(params Assembly[] assemblies)
    {
        _assemblies = assemblies;
    }

    public IEnumerable<ToolEntry> Discover()
    {
        foreach (var type in _assemblies.SelectMany(assembly => assembly.GetTypes()))
        {
            if (type.IsAbstract || !typeof(AgentTool).IsAssignableFrom(type))
            {
                continue;
            }
            if (type.GetCustomAttribute<ToolAttribute>() is { } attribute)
            {
                yield return new ToolEntry
                {
                    Definition = CreateDefinition(type),
                    Execute = async (arguments, context, cancellationToken) =>
                    {
                        var tool = (AgentTool)Activator.CreateInstance(type)!;
                        BindParameters(type, tool, arguments);
                        var text = await tool.ExecuteAsync(context, cancellationToken);
                        return new ToolExecutionResult(text, tool);
                    }
                };
            }
        }
    }

    private static void BindParameters(Type type, AgentTool tool, JsonObject arguments)
    {
        foreach (var property in type.GetProperties())
        {
            var attribute = property.GetCustomAttribute<ToolParameterAttribute>();
            if (attribute is null)
            {
                continue;
            }
            var jsonName = attribute.Name ?? ToSnakeCase(property.Name);
            if (arguments.TryGetPropertyValue(jsonName, out var node) && node is not null)
            {
                var raw = node is JsonValue value && value.TryGetValue<string>(out var str)
                    ? str
                    : node.ToJsonString();
                property.SetValue(tool, Convert(raw ?? string.Empty, property.PropertyType));
            }
            else if (attribute.Required)
            {
                throw new InvalidOperationException($"missing required parameter '{jsonName}'");
            }
        }
    }

    private static object? Convert(string value, Type targetType)
    {
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (type == typeof(string))
        {
            return value;
        }
        if (type == typeof(bool))
        {
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
        }
        if (type == typeof(int))
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
        }
        if (type == typeof(long))
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0L;
        }
        if (type == typeof(double))
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0.0;
        }
        if (type == typeof(float))
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0f;
        }
        if (type.IsEnum)
        {
            return Enum.TryParse(type, value, ignoreCase: true, out var result) ? result : Activator.CreateInstance(type);
        }
        if (type == typeof(object))
        {
            return TryParseJsonNode(value) ?? value;
        }
        if (IsJsonContainer(type))
        {
            return ConvertContainer(value, type);
        }
        return value;
    }

    /// <summary>
    /// Массивы/списки/словари: парсер приносит значение строкой — разбираем JSON-декодером.
    /// Если это не JSON (например, модель написала одно значение без скобок) — коллекция
    /// из одного элемента для массивов/списков, иначе null.
    /// </summary>
    private static object? ConvertContainer(string value, Type type)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize(value, type);
            if (parsed is not null)
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
        }
        return CreateSingleElementCollection(value, type);
    }

    private static object? CreateSingleElementCollection(string value, Type type)
    {
        if (type == typeof(string[]))
        {
            return new[] { value };
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) &&
            type.GetGenericArguments()[0] == typeof(string))
        {
            return new List<string> { value };
        }
        return null;
    }

    private static bool IsJsonContainer(Type type) =>
        type.IsArray ||
        type.IsGenericType &&
        type.GetGenericTypeDefinition() is var definition &&
        (definition == typeof(List<>) ||
         definition == typeof(IReadOnlyList<>) ||
         definition == typeof(IEnumerable<>) ||
         definition == typeof(HashSet<>) ||
         definition == typeof(Dictionary<,>) ||
         definition == typeof(IDictionary<,>));

    private static JsonNode? TryParseJsonNode(string value)
    {
        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ToolDefinition CreateDefinition(Type type)
    {
        var attribute = type.GetCustomAttribute<ToolAttribute>()!;
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var property in type.GetProperties())
        {
            var parameter = property.GetCustomAttribute<ToolParameterAttribute>();
            if (parameter is null)
            {
                continue;
            }
            var jsonName = parameter.Name ?? ToSnakeCase(property.Name);
            properties[jsonName] = new JsonObject
            {
                ["type"] = JsonTypeOf(property.PropertyType),
                ["description"] = parameter.Description
            };
            if (parameter.Required)
            {
                required.Add(jsonName);
            }
        }

        var parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0)
        {
            parameters["required"] = required;
        }

        return new ToolDefinition
        {
            Name = attribute.Name,
            Description = attribute.Description,
            Parameters = parameters
        };
    }

    private static string JsonTypeOf(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short) || underlying == typeof(byte))
        {
            return "integer";
        }
        if (underlying == typeof(double) || underlying == typeof(float) || underlying == typeof(decimal))
        {
            return "number";
        }
        if (underlying == typeof(bool))
        {
            return "boolean";
        }
        if (underlying != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(underlying))
        {
            return "array";
        }
        return "string";
    }

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }
}
