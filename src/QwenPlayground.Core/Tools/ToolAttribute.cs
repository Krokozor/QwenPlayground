namespace QwenPlayground.Core.Tools;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; }

    public ToolAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }
}
