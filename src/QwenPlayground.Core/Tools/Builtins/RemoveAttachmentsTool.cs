using System.IO;
using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Удаляет все вложения (артефакты) из сообщения по его стабильному ID. Освобождает контекст:
/// после удаления маркеры исчезают из рендера, base64 не отправляется серверу.
/// Рабочий паттерн: load_image → посмотрел → remove_attachments → контекст чист.
/// </summary>
[Tool("remove_attachments",
    "Remove all attachments (images) from a message by its stable ID. This frees context: the " +
    "markers disappear from the render and the base64 is no longer sent to the server. Use after " +
    "you are done looking at loaded images. Returns the number of files removed.")]
public sealed class RemoveAttachmentsTool : AgentTool
{
    [ToolParameter("Stable message ID to remove attachments from.", Required = true)]
    public int MsgId { get; set; }

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.SessionDir is null)
            return Task.FromResult("Error: no session directory.");

        var store = new MessageMetaStore(context.SessionDir);
        var removed = store.RemoveArtifacts(MsgId);
        // Локальный load_image (до введения финализации) мог оставить артефакты в msg_0 —
        // на всякий случай чистим и их, если по MsgId ничего не нашлось.
        if (removed == 0 && MsgId != 0)
        {
            removed = store.RemoveArtifacts(0);
        }
        return Task.FromResult(removed > 0
            ? $"Removed {removed} attachment(s) from message {MsgId}."
            : $"No attachments found for message {MsgId}.");
    }
}
