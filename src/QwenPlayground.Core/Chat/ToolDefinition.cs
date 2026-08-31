using System.Text.Json.Nodes;

namespace QwenPlayground.Core.Chat;

public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonNode Parameters { get; init; }
}
