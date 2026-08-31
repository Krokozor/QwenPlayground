namespace QwenPlayground.Core.Chat;

public sealed class GenerationInfo
{
    public required string Prompt { get; init; }
    public string RawOutput { get; set; } = string.Empty;
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
}
