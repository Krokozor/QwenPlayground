using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailKit.Search;
using MimeKit;

namespace QwenPlayground.Core.Mail;

/// <summary>Минимальная граница IMAP для монитора: позволяет тестировать его без сети.</summary>
public interface IMailConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct = default);
    Task<MailUnreadResult> GetUnreadAsync(string folderName, int max = 20, CancellationToken ct = default);
}

/// <summary>
/// Почтовый клиент (IMAP/SMTP) на MailKit 4.x. Процессный сервис: одно IMAP-соединение на
/// процесс, шарится всеми сессиями. Per-session состояние (курсоры прочтения) — в MailSession.
///
/// OAuth2 (Google): access token из refresh token (POST token.googleapis.com), кэш на 50 мин.
/// PLAIN (пароль) — для не-Google провайдеров (у Google блокируется).
///
/// Тред-модель: I/O — фоновые потоки (MailKit асинхронный); мутация ядра — UI-поток.
/// </summary>
public sealed class MailService : IMailConnection
{
    private ImapClient? _imap;
    private readonly object _gate = new();
    private string? _cachedAccessToken;
    private DateTime _accessTokenExpiry;

    /// <summary>Живое IMAP-соединение, а не исторический флаг успешного подключения.</summary>
    public bool IsConnected
    {
        get { lock (_gate) return _imap is { IsConnected: true, IsAuthenticated: true }; }
    }

    /// <summary>Email аккаунта (из настроек; для display).</summary>
    public string Email => MailSettings.Get().Email;

    /// <summary>Подключить и авторизовать IMAP. OAuth2 (refresh token) или PLAIN (пароль).</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var s = MailSettings.Get();
        var imap = new ImapClient();
        try
        {
            await imap.ConnectAsync(s.ImapHost, s.ImapPort, SecureSocketOptions.SslOnConnect, ct);

            if (!string.IsNullOrWhiteSpace(s.RefreshToken))
            {
                var token = await GetAccessTokenAsync(ct);
                await imap.AuthenticateAsync(new SaslMechanismOAuth2(s.Email, token), ct);
            }
            else if (!string.IsNullOrEmpty(s.Password))
            {
                await imap.AuthenticateAsync(s.Email, s.Password, ct);
            }
            else
            {
                throw new InvalidOperationException(
                    "Mail not configured: no refresh token and no password. " +
                    "Use mail_auth (OAuth2) or set Mail.Password (set_setting / Настройки).");
            }

            lock (_gate)
            {
                _imap?.Dispose();
                _imap = imap;
            }
        }
        catch
        {
            imap.Dispose();
            throw;
        }
    }

    /// <summary>Получить access token из refresh token (OAuth2). Кэш на 50 минут.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        string? cached;
        lock (_gate)
        {
            cached = _cachedAccessToken;
        }
        if (cached is not null && DateTime.UtcNow < _accessTokenExpiry)
        {
            return cached;
        }

        var s = MailSettings.Get();
        using var http = new HttpClient();
        using var resp = await http.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = s.RefreshToken,
                ["client_id"] = s.OAuthClientId,
                ["client_secret"] = s.OAuthClientSecret,
            }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OAuth token refresh failed ({(int)resp.StatusCode}): {body}");
        }
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("OAuth response missing access_token");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

        lock (_gate)
        {
            _cachedAccessToken = token;
            _accessTokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
        }
        Security.SecretRedactor.Register(token);
        return token;
    }

    /// <summary>Список папок (рекурсивно).</summary>
    public async Task<IReadOnlyList<IMailFolder>> GetFoldersAsync(CancellationToken ct = default)
    {
        var imap = RequireImap();
        var ns = new FolderNamespace('\0', "");
        return (await imap.GetFoldersAsync(ns, true, ct)).ToList();
    }

    /// <summary>Папка по имени (null, если нет).</summary>
    public async Task<IMailFolder?> GetFolderAsync(string name, CancellationToken ct = default)
    {
        var imap = RequireImap();
        try
        {
            return await imap.GetFolderAsync(name, ct);
        }
        catch (ImapProtocolException)
        {
            return null;
        }
    }

    /// <summary>Непрочитанные письма в папке (последние max) и полный счётчик.</summary>
    public async Task<MailUnreadResult> GetUnreadAsync(string folderName, int max = 20, CancellationToken ct = default)
    {
        var folder = await GetFolderAsync(folderName, ct) ?? throw new InvalidOperationException($"Folder not found: {folderName}");
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var uids = (await folder.SearchAsync(SearchQuery.NotSeen, ct)).ToList();
        if (uids.Count == 0) return new MailUnreadResult(0, []);
        var start = Math.Max(0, uids.Count - Math.Max(1, max));
        var last = uids.GetRange(start, uids.Count - start);
        var summaries = await folder.FetchAsync(last, new FetchRequest(
            MessageSummaryItems.Envelope |
            MessageSummaryItems.Flags |
            MessageSummaryItems.UniqueId |
            MessageSummaryItems.PreviewText), ct);

        var messages = summaries
            .Where(s => s.Flags is null || !s.Flags.Value.HasFlag(MessageFlags.Seen))
            .Select(s => new MailMessage(
                s.UniqueId,
                s.Envelope?.Subject ?? "(no subject)",
                s.Envelope?.From?.ToString() ?? "(unknown)",
                s.Date,
                s.PreviewText ?? ""))
            .ToList();
        return new MailUnreadResult(uids.Count, messages);
    }

    /// <summary>Полное письмо по UniqueId.</summary>
    public async Task<MailMessageFull> GetMessageAsync(UniqueId uid, string folderName = "INBOX", CancellationToken ct = default)
    {
        var folder = await GetFolderAsync(folderName, ct) ?? throw new InvalidOperationException($"Folder not found: {folderName}");
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var msg = await folder.GetMessageAsync(uid, ct, null);
        return new MailMessageFull(
            uid,
            msg.Subject ?? "(no subject)",
            msg.From?.ToString() ?? "(unknown)",
            msg.To?.ToString() ?? "",
            msg.Date,
            msg.TextBody ?? msg.HtmlBody ?? "(no body)",
            msg.Attachments.Count(),
            msg.MessageId ?? "");
    }

    /// <summary>Отметить письмо как прочитанное.</summary>
    public async Task MarkSeenAsync(UniqueId uid, string folderName = "INBOX", CancellationToken ct = default)
    {
        var folder = await GetFolderAsync(folderName, ct) ?? throw new InvalidOperationException($"Folder not found: {folderName}");
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        await folder.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen), ct);
    }

    /// <summary>Переместить письмо в другую папку.</summary>
    public async Task MoveAsync(UniqueId uid, string fromFolder, string toFolder, CancellationToken ct = default)
    {
        var src = await GetFolderAsync(fromFolder, ct) ?? throw new InvalidOperationException($"Folder not found: {fromFolder}");
        var dst = await GetFolderAsync(toFolder, ct) ?? throw new InvalidOperationException($"Folder not found: {toFolder}");
        await src.OpenAsync(FolderAccess.ReadWrite, ct);
        await src.MoveToAsync(uid, dst, ct);
    }

    /// <summary>Отправить письмо (SMTP).</summary>
    public async Task SendAsync(string to, string subject, string body, string? cc = null, CancellationToken ct = default)
    {
        var s = MailSettings.Get();
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("", s.Email));
        msg.To.Add(new MailboxAddress("", to));
        if (cc is not null) msg.Cc.Add(new MailboxAddress("", cc));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };
        await SendMessageAsync(msg, s, ct);
    }

    /// <summary>Ответ на письмо с заголовками threading для почтовых клиентов.</summary>
    public async Task ReplyAsync(MailMessageFull original, string replyTo, string body, CancellationToken ct = default)
    {
        var s = MailSettings.Get();
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("", s.Email));
        msg.To.Add(new MailboxAddress("", replyTo));
        msg.Subject = original.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? original.Subject
            : $"Re: {original.Subject}";
        msg.Body = new TextPart("plain") { Text = body };
        if (!string.IsNullOrWhiteSpace(original.MessageId))
        {
            msg.InReplyTo = original.MessageId;
            msg.References.Add(original.MessageId);
        }
        await SendMessageAsync(msg, s, ct);
    }

    private async Task SendMessageAsync(MimeMessage msg, MailSettings s, CancellationToken ct)
    {
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(s.SmtpHost, s.SmtpPort, SecureSocketOptions.SslOnConnect, ct);

        if (!string.IsNullOrWhiteSpace(s.RefreshToken))
        {
            var token = await GetAccessTokenAsync(ct);
            await smtp.AuthenticateAsync(new SaslMechanismOAuth2(s.Email, token), ct);
        }
        else if (!string.IsNullOrEmpty(s.Password))
        {
            await smtp.AuthenticateAsync(s.Email, s.Password, ct);
        }
        else
        {
            throw new InvalidOperationException("Mail not configured: no refresh token and no password.");
        }

        await smtp.SendAsync(msg, ct);
    }

    /// <summary>Скачать вложение в файл, не покидая saveTo и не перезаписывая существующие файлы.</summary>
    public async Task<string> DownloadAttachmentAsync(UniqueId uid, int attachmentIndex, string saveTo, string folderName = "INBOX", CancellationToken ct = default)
    {
        var folder = await GetFolderAsync(folderName, ct) ?? throw new InvalidOperationException($"Folder not found: {folderName}");
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var msg = await folder.GetMessageAsync(uid, ct, null);
        var attachments = msg.Attachments.ToList();
        if (attachmentIndex < 0 || attachmentIndex >= attachments.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(attachmentIndex), $"Attachment index {attachmentIndex}, message has {attachments.Count}");
        }
        var att = attachments[attachmentIndex] as MimePart
            ?? throw new InvalidOperationException($"Attachment {attachmentIndex} is not a MimePart");
        var content = att.Content
            ?? throw new InvalidOperationException($"Attachment {attachmentIndex} has no content");
        var path = ResolveAttachmentPath(saveTo, att.FileName, attachmentIndex);
        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.DecodeToAsync(fs, ct);
        return path;
    }

    internal static string ResolveAttachmentPath(string saveTo, string? fileName, int attachmentIndex)
    {
        var root = Path.GetFullPath(saveTo);
        Directory.CreateDirectory(root);
        var name = Path.GetFileName(fileName ?? string.Empty);
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }
        if (string.IsNullOrWhiteSpace(name)) name = $"attachment_{attachmentIndex}";

        var candidate = Path.Combine(root, name);
        if (File.Exists(candidate))
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            for (var i = 2; i < 1000 && File.Exists(candidate); i++)
            {
                candidate = Path.Combine(root, $"{stem} ({i}){extension}");
            }
            if (File.Exists(candidate))
            {
                candidate = Path.Combine(root, $"{stem}-{Guid.NewGuid():N}{extension}");
            }
        }

        var full = Path.GetFullPath(candidate);
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Attachment path escapes the destination directory.");
        }
        return full;
    }

    /// <summary>IMAP IDLE: ждать новые письма (timeout секунд, 0 = бесконечно).</summary>
    public async Task IdleAsync(int timeoutSeconds, CancellationToken ct = default)
    {
        var imap = RequireImap();
        var idleCts = timeoutSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        try
        {
            if (idleCts is not null)
            {
                idleCts.CancelAfter(timeoutSeconds * 1000);
                await imap.IdleAsync(idleCts.Token, ct);
            }
            else
            {
                await imap.IdleAsync(CancellationToken.None, ct);
            }
        }
        finally
        {
            idleCts?.Dispose();
        }
    }

    private ImapClient RequireImap()
    {
        lock (_gate)
        {
            var imap = _imap ?? throw new InvalidOperationException("Mail not connected. Call ConnectAsync first.");
            if (!imap.IsConnected || !imap.IsAuthenticated) throw new InvalidOperationException("Mail IMAP disconnected.");
            return imap;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _imap?.Dispose();
            _imap = null;
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Короткое письмо (список непрочитанных).</summary>
public record MailMessage(UniqueId Uid, string Subject, string From, DateTimeOffset Date, string Snippet);

/// <summary>Последние непрочитанные письма и полный счётчик, полученные в одном поиске.</summary>
public record MailUnreadResult(int TotalCount, IReadOnlyList<MailMessage> Messages);

/// <summary>Полное письмо (тело + метаданные).</summary>
public record MailMessageFull(
    UniqueId Uid, string Subject, string From, string To,
    DateTimeOffset Date, string Body, int AttachmentCount, string MessageId);
