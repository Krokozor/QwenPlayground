namespace QwenPlayground.Core.Tools.Builtins;

[Tool("ask_user", "Ask the user a question and wait for their answer. Use when you need a decision, clarification or confirmation mid-task.")]
public sealed class AskUserTool : AgentTool
{
    [ToolParameter("Question to ask the user", Required = true)]
    public string Question { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var pending = context.Scope.TryAsk(Question, cancellationToken);
        if (pending is null)
        {
            return "Error: no user is available in this context";
        }
        var answer = await pending;
        return answer.Trim().Length > 0 ? answer : "(empty answer)";
    }
}
