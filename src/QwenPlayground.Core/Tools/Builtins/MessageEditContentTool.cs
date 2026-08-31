namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Редактирует контент сообщения по стабильному ID (&lt;id=N&gt;). Юзкейсы:
/// — выбросить большой tool-вывод (заменить коротким указателем, освободив контекст);
/// — сохранить временный артефакт (скриншот, большой вывод) в файл и заменить ссылкой.
/// Сами сообщение НЕ удаляем: меняется только контент, что сохраняет pairing
/// tool_call→tool_result и стабильность ID (см. trajectory/backlog «Мета-данные по ID»).
/// </summary>
[Tool("message_edit_content",
    "Edit the content of a message by its stable <id=N> (the id shown at the start of the message). " +
    "Use to discard a large tool result (replace it with a short pointer to free context) or to save it to a file first. " +
    "The message is NOT deleted — only its content changes, preserving tool_call→tool_result pairing and id stability.")]
public sealed class MessageEditContentTool : AgentTool
{
    [ToolParameter("The stable message id (the <id=N> shown at the start of the message).", Required = true)]
    public int MsgId { get; set; }

    [ToolParameter("New content to replace the message's content with (e.g. a short pointer like 'result saved to file X').", Required = true)]
    public string NewContent { get; set; } = string.Empty;

    [ToolParameter("Optional: relative path to save the OLD content to before replacing (persist a large tool output / temp artifact).", Required = false)]
    public string? SaveOldToFile { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.GetMessageById is null || context.SetMessageContent is null)
        {
            return "Error: message editing is not available in this context.";
        }
        var message = context.GetMessageById(MsgId);
        if (message is null)
        {
            return $"Error: no message with id {MsgId} (it may have been compacted or does not exist).";
        }
        var oldContent = message.Content;
        string savedNote = string.Empty;
        if (!string.IsNullOrWhiteSpace(SaveOldToFile))
        {
            try
            {
                var path = context.ResolvePath(SaveOldToFile);
                await File.WriteAllTextAsync(path, oldContent, cancellationToken);
                savedNote = $", old content saved to {SaveOldToFile} ({oldContent.Length} chars)";
            }
            catch (Exception exception)
            {
                return $"Error: could not save old content to '{SaveOldToFile}': {exception.Message}";
            }
        }
        if (!context.SetMessageContent(MsgId, NewContent))
        {
            return $"Error: could not edit message {MsgId}.";
        }
        return $"Edited message {MsgId}: replaced {oldContent.Length} chars with {NewContent.Length} chars{savedNote}.";
    }
}
