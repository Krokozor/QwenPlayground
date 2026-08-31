namespace QwenPlayground.Core.Tools;

[AttributeUsage(AttributeTargets.Property)]
public sealed class ToolParameterAttribute : Attribute
{
    public string? Name { get; set; }
    public string Description { get; set; }
    public bool Required { get; set; }

    public ToolParameterAttribute(string description = "")
    {
        Description = description;
    }
}
