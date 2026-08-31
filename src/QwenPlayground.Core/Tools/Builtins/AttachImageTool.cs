using System.IO;
using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Прикрепляет файл (картинку/документ) к сообщению по стабильному ID: копирует его в
/// папку артефактов сессии (sessions/main/artifacts/msg_&lt;id&gt;/). При рендере сообщения
/// файл включается в промпт (мультимодальность: маркер + base64). Файл копируется (не ссылка)
/// — ресурс живёт вместе с сессией и чистится при компакции сообщения.
/// </summary>
[Tool("attach_image",
    "Attach a file (image/document) to a message by its stable <id=N>. The file is copied into the " +
    "session's artifact folder (sessions/main/artifacts/msg_<id>/) and will be rendered into the prompt " +
    "when that message is included (multimodal: the model will see the image). Use to give the model a " +
    "picture or document to look at.")]
public sealed class AttachImageTool : AgentTool
{
    [ToolParameter("The stable message ID to attach the file to (the <id=N> shown at the start of the message).", Required = true)]
    public int MsgId { get; set; }

    [ToolParameter("Path to the file (relative to the workspace or absolute).", Required = true)]
    public string FilePath { get; set; } = string.Empty;

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        string source;
        try
        {
            source = context.ResolvePath(FilePath);
        }
        catch (Exception exception)
        {
            return Task.FromResult($"Error: {exception.Message}");
        }
        if (!File.Exists(source))
        {
            return Task.FromResult($"Error: file not found: {FilePath}");
        }
        // Сессия, где реально идёт диалог: артефакты должны попасть в её папку
        // (sessions/<id>/artifacts/msg_<id>/), иначе рендер их не найдёт.
        // Fallback на sessions/main — для контекстов без сессии (оркестратор).
        var sessionDir = context.SessionDir
            ?? System.IO.Path.Combine(context.ProjectRoot, "sessions", "main");
        try
        {
            var store = new MessageMetaStore(sessionDir);
            var dest = store.AddArtifact(MsgId, source);
            var size = new FileInfo(dest).Length;
            return Task.FromResult($"Attached '{FilePath}' to message {MsgId} ({size} bytes), stored at {dest}.");
        }
        catch (Exception exception)
        {
            return Task.FromResult($"Error: could not attach file: {exception.Message}");
        }
    }
}
