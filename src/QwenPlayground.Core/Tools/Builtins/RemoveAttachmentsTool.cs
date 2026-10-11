using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Удаляет все вложения (артефакты) из одного или нескольких сообщений по их стабильным ID.
/// Освобождает контекст: после удаления маркеры исчезают из рендера, base64 не отправляется
/// серверу. Рекомендуемый паттерн: load_image → посмотрел → remove_attachments → контекст чист
/// (несколько ID — одной командой, без по одному).
/// </summary>
[Tool("remove_attachments",
    "Remove all attachments (images) from one or more messages by their stable IDs. This frees " +
    "context: the markers disappear from the render and the base64 is no longer sent to the server. " +
    "Use after you are done looking at loaded images; pass several IDs to clean up multiple " +
    "messages in one call. Returns the number of files removed.")]
public sealed class RemoveAttachmentsTool : AgentTool
{
    [ToolParameter("Stable message IDs to remove attachments from (one or more).", Required = true)]
    public int[] MsgIds { get; set; } = Array.Empty<int>();

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.SessionDir is null)
            return Task.FromResult("Error: no session directory.");

        if (MsgIds is null || MsgIds.Length == 0)
            return Task.FromResult("No message IDs provided.");

        var store = new MessageMetaStore(context.SessionDir);
        var ids = MsgIds.Distinct().ToArray();
        var details = new List<string>(ids.Length);
        var total = 0;

        foreach (var id in ids)
        {
            var removed = store.RemoveArtifacts(id);
            // Локальный load_image (до введения финализации) мог оставить артефакты в msg_0 —
            // на всякий случай чистим и их, если по заданному ID ничего не нашлось.
            if (removed == 0 && id != 0)
            {
                removed = store.RemoveArtifacts(0);
            }
            total += removed;
            details.Add($"{id}→{removed}");
        }

        return Task.FromResult(total > 0
            ? $"Removed {total} attachment(s) in total ({string.Join(", ", details)})."
            : $"No attachments found for message(s): {string.Join(", ", ids)}.");
    }
}
