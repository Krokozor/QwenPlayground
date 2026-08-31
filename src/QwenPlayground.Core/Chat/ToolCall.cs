using System.Text.Json.Nodes;

namespace QwenPlayground.Core.Chat;

public sealed class ToolCall
{
    public required string Name { get; init; }
    public required JsonNode Arguments { get; init; }
}
