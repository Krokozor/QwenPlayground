namespace QwenPlayground.Core.Mail;

/// <summary>
/// Действие runtime-синка почты после изменения настроек (план, раздел 15).
/// </summary>
public enum MailRuntimeAction
{
    /// <summary>Ничего делать не нужно (изменения не требуют реакции runtime).</summary>
    None,

    /// <summary>Запустить монитор (почта вкл и настроена, монитор не запущен).</summary>
    Start,

    /// <summary>Остановить монитор (почта выкл или не настроена).</summary>
    Stop,

    /// <summary>Полный рестарт: отключить монитор и кэшированные соединения, затем
    /// запустить заново (сменились connection-поля — старое IMAP-соединение устарело).</summary>
    Restart
}

/// <summary>
/// Решение runtime-синка почтовой подсистемы после изменения настроек (план, раздел 15).
/// Чистая функция (previous → current → действие) — тестируется без статиков.
///
/// Контекст: после мержа mail.json в settings.json (раздел 14) set_setting стал
/// писателем почтовых настроек, а до этого писателями были только UI и mail_auth,
/// которые сами управляли стартом/стопом. Без синка возможны состояния: Enabled=true
/// записан, но монитор не запущен; Enabled=false, но опросы идут; ImapHost изменён,
/// а соединение осталось со старым сервером.
///
/// Правила:
/// - connection-поля (хосты, порты, credentials) сменились → Restart (у монитора и
///   MailHolder кэшируются IMAP/SMTP-соединения — оба кэша устаревают);
/// - Enabled && IsConfigured → монитор должен работать, иначе — нет (Start/Stop);
/// - PollIntervalSeconds/WakePolicy/WakeSenders/WakeFolders/Email читаются монитором
///   в полёте (каждый цикл) — рестарт не нужен;
/// - previous == null → None (базовый снимок: состояние на старте обрабатывает MainViewModel).
/// </summary>
public static class MailRuntime
{
    /// <summary>Сменились ли поля, определяющие соединение (старый коннект устарел).</summary>
    public static bool ConnectionChanged(MailSettings a, MailSettings b) =>
        a.ImapHost != b.ImapHost ||
        a.ImapPort != b.ImapPort ||
        a.SmtpHost != b.SmtpHost ||
        a.SmtpPort != b.SmtpPort ||
        a.Password != b.Password ||
        a.RefreshToken != b.RefreshToken ||
        a.OAuthClientSecret != b.OAuthClientSecret ||
        a.OAuthClientId != b.OAuthClientId;

    public static MailRuntimeAction Decide(MailSettings? previous, MailSettings current)
    {
        if (previous is null)
        {
            return MailRuntimeAction.None;
        }

        var shouldRun = current.Enabled && current.IsConfigured;
        var wasRunning = previous.Enabled && previous.IsConfigured;

        if (ConnectionChanged(previous, current))
        {
            return shouldRun ? MailRuntimeAction.Restart : MailRuntimeAction.Stop;
        }
        if (shouldRun && !wasRunning)
        {
            return MailRuntimeAction.Start;
        }
        if (!shouldRun && wasRunning)
        {
            return MailRuntimeAction.Stop;
        }
        return MailRuntimeAction.None;
    }
}
