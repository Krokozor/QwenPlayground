using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QwenPlayground.Core.Serialization;

public static class PythonStyleJson
{
    private static readonly JsonSerializerOptions StringOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(node, builder);
        return builder.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
            {
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in obj)
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }
                    first = false;
                    builder.Append(JsonSerializer.Serialize(key, StringOptions));
                    builder.Append(": ");
                    Write(value, builder);
                }
                builder.Append('}');
                break;
            }
            case JsonArray array:
            {
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }
                    Write(array[i], builder);
                }
                builder.Append(']');
                break;
            }
            case JsonValue value:
                builder.Append(value.TryGetValue<string>(out var str)
                    ? JsonSerializer.Serialize(str, StringOptions)
                    : value.ToJsonString());
                break;
        }
    }
}
