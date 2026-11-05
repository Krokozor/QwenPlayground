using System.IO;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Tools;

/// <summary>
/// Почта (IMAP/SMTP): тулы полки "mail". Входящие — в state-блоке (фаза 2), здесь —
/// чтение/отправка/архивация/вложения. Не настроено — дружелюбный отказ с подсказкой.
/// </summary>

[Tool("mail_check", "Check unread mail: lists the last N unread messages in a folder (default INBOX). " +
    "Each line: uid, date, from, subject, snippet. Use to see what's new without reading full messages. " +
    "The 'mail' line in <state> shows the unread count — this tool gives the details.",
    group: ToolGroup.Mail)]
public sealed class MailCheckTool : AgentTool
{
    [ToolParameter("Folder name (default INBOX)")]
    public string Folder { get; set; } = "INBOX";

    [ToolParameter("Max messages to list (default 10, max 50)")]
    public int Count { get; set; } = 10;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var mail = await MailHolder.GetOrConnectAsync(cancellationToken);
            var result = await mail.GetUnreadAsync(Folder, Math.Clamp(Count, 1, 50), cancellationToken);
            if (result.TotalCount == 0)
            {
                return $"{Folder}: no unread messages.";
            }
            var lines = new List<string> { $"{Folder}: {result.TotalCount} unread (showing last {result.Messages.Count}):" };
            foreach (var m in result.Messages)
            {
                lines.Add($"  [{MailHolder.UidToString(m.Uid)}] {m.Date:yyyy-MM-dd HH:mm} | {m.From} | {m.Subject}" +
                          (string.IsNullOrWhiteSpace(m.Snippet) ? "" : $" — \"{m.Snippet}\""));
            }
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"mail_check failed: {ex.Message}";
        }
    }
}

[Tool("mail_read", "Read a full message by uid (from mail_check). Returns subject, from, to, date, body, " +
    "attachment count. Marks the message as read. Use after mail_check to read a specific message.",
    group: ToolGroup.Mail)]
public sealed class MailReadTool : AgentTool
{
    [ToolParameter("Message uid (from mail_check, e.g. '1234' or '1234:5678')", Required = true)]
    public string Uid { get; set; } = "";

    [ToolParameter("Folder name (default INBOX)")]
    public string Folder { get; set; } = "INBOX";

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var mail = await MailHolder.GetOrConnectAsync(cancellationToken);
            var uid = MailHolder.ParseUid(Uid);
            var msg = await mail.GetMessageAsync(uid, Folder, cancellationToken);
            await mail.MarkSeenAsync(uid, Folder, cancellationToken);
            return $"From: {msg.From}\nTo: {msg.To}\nDate: {msg.Date:yyyy-MM-dd HH:mm}\nSubject: {msg.Subject}\n" +
                   $"Attachments: {msg.AttachmentCount}\nMessageId: {msg.MessageId}\n\n{msg.Body}";
        }
        catch (Exception ex)
        {
            return $"mail_read failed: {ex.Message}";
        }
    }
}

[Tool("mail_send", "Send an email. Parameters: to (recipient), subject, body (plain text). Optional cc. " +
    "Use when the owner asks to send an email or when a task requires notifying someone by mail.",
    group: ToolGroup.Mail)]
public sealed class MailSendTool : AgentTool
{
    [ToolParameter("Recipient email address", Required = true)]
    public string To { get; set; } = "";

    [ToolParameter("Subject", Required = true)]
    public string Subject { get; set; } = "";

    [ToolParameter("Body (plain text)", Required = true)]
    public string Body { get; set; } = "";

    [ToolParameter("CC (optional)")]
    public string? Cc { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var mail = await MailHolder.GetOrConnectAsync(cancellationToken);
            await mail.SendAsync(To, Subject, Body, Cc, cancellationToken);
            return $"Sent to {To}: \"{Subject}\"";
        }
        catch (Exception ex)
        {
            return $"mail_send failed: {ex.Message}";
        }
    }
}

[Tool("mail_reply", "Reply to a message by uid. Fetches the original subject and Message-ID, " +
    "sets In-Reply-To/References for proper threading. Parameters: uid, reply_to, body, optional folder.",
    group: ToolGroup.Mail)]
public sealed class MailReplyTool : AgentTool
{
    [ToolParameter("Original message uid (from mail_check)", Required = true)]
    public string Uid { get; set; } = "";

    [ToolParameter("Recipient email (usually the original sender)", Required = true)]
    public string ReplyTo { get; set; } = "";

    [ToolParameter("Reply body (plain text)", Required = true)]
    public string Body { get; set; } = "";

    [ToolParameter("Folder name (default INBOX)")]
    public string Folder { get; set; } = "INBOX";

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var mail = await MailHolder.GetOrConnectAsync(cancellationToken);
            var uid = MailHolder.ParseUid(Uid);
            var original = await mail.GetMessageAsync(uid, Folder, cancellationToken);
            await mail.ReplyAsync(original, ReplyTo, Body, cancellationToken);
            var subject = original.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                ? original.Subject : $"Re: {original.Subject}";
            return $"Replied to {ReplyTo}: \"{subject}\"";
        }
        catch (Exception ex)
        {
            return $"mail_reply failed: {ex.Message}";
        }
    }
}

[Tool("mail_move", "Move a message to another folder (archive, spam, etc.). Parameters: uid, from_folder " +
    "(default INBOX), to_folder. Use for organizing: archive old mail, move spam to Junk.",
    group: ToolGroup.Mail)]
public sealed class MailMoveTool : AgentTool
{
    [ToolParameter("Message uid (from mail_check)", Required = true)]
    public string Uid { get; set; } = "";

    [ToolParameter("Source folder (default INBOX)")]
    public string FromFolder { get; set; } = "INBOX";

    [ToolParameter("Destination folder (e.g. 'INBOX/Archive', 'Junk')", Required = true)]
    public string ToFolder { get; set; } = "";

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var mail = await MailHolder.GetOrConnectAsync(cancellationToken);
            var uid = MailHolder.ParseUid(Uid);
            await mail.MoveAsync(uid, FromFolder, ToFolder, cancellationToken);
            return $"Moved [{Uid}] from {FromFolder} to {ToFolder}.";
        }
        catch (Exception ex)
        {
            return $"mail_move failed: {ex.Message}";
        }
    }
}

[Tool("mail_download_attachment", "Download an attachment from a message to the session's artifacts folder. " +
    "Parameters: uid (message), attachment_index (0-based, from mail_read's 'Attachments: N'), folder (default INBOX). " +
    "Returns the file path. Use when a message has an attachment you need to read (PDF, image, etc.).",
    group: ToolGroup.Mail)]
public sealed class MailDownloadAttachmentTool : AgentTool
{
    [ToolParameter("Message uid (from mail_check)", Required = true)]
    public string Uid { get; set; } = "";

    [ToolParameter("Attachment index (0-based; mail_read shows 'Attachments: N')", Required = true)]
    public int AttachmentIndex { get; set; }

    [ToolParameter("Folder name (default INBOX)")]
    public string Folder { get; set; } = "INBOX";

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var mail = await MailHolder.GetOrConnectAsync(cancellationToken);
            var uid = MailHolder.ParseUid(Uid);
            var saveTo = context.SessionDir is not null
                ? Path.Combine(context.SessionDir, "artifacts", "mail")
                : Path.Combine(context.ProjectRoot, "downloads");
            Directory.CreateDirectory(saveTo);
            var path = await mail.DownloadAttachmentAsync(uid, AttachmentIndex, saveTo, Folder, cancellationToken);
            return $"Downloaded to: {path}";
        }
        catch (Exception ex)
        {
            return $"mail_download_attachment failed: {ex.Message}";
        }
    }
}
