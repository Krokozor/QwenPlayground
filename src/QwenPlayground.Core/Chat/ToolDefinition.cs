using System.Text.Json.Nodes;

namespace QwenPlayground.Core.Chat;

public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonNode Parameters { get; init; }

    /// <summary>Группа (полка): Core — всегда в промпте; остальные — активируются по требованию.</summary>
    public ToolGroup Group { get; init; } = ToolGroup.Core;
}
