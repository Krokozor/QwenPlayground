using QwenPlayground.Core.Chat;

namespace QwenPlayground.Core.Tools;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; }

    /// <summary>Группа (полка): Core — всегда в промпте; Browser/CSharp — активируются по требованию.</summary>
    public ToolGroup Group { get; }

    public ToolAttribute(string name, string description, ToolGroup group = ToolGroup.Core)
    {
        Name = name;
        Description = description;
        Group = group;
    }
}
