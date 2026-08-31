namespace QwenPlayground.Core.Inference;

public sealed record TokenUsage(int? PromptTokens, int? CompletionTokens);

public sealed record CompletionResult(string Text, TokenUsage? Usage);
