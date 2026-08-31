using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Inference;

public sealed class GenerationOptions
{
    public int MaxTokens { get; init; } = 1024;
    public double Temperature { get; init; } = 0.7;
    public double TopP { get; init; } = 0.8;
    public int TopK { get; init; } = 20;
    public double MinP { get; init; }
    public double RepeatPenalty { get; init; } = 1.05;
    public int? Seed { get; init; }
    public IReadOnlyList<string> Stop { get; init; } = [QwenSpecialTokens.ImEnd, QwenSpecialTokens.EndOfText];
}
