using QwenPlayground.Core.Mail;

namespace QwenPlayground.App.Tools;

/// <summary>
/// Runtime-синк почтовой подсистемы после изменения AppSettings (план, раздел 15).
/// Вызывается на UI-потоке (из RefreshSettingsBindings): MailMonitor.Stop блокирует
/// до 2 секунд (loop.Wait), а poll-цикл живёт на UI-контексте.
/// Идемпотентен: повторные вызовы без изменений почты → Decide = None; TryStart/Stop
/// сами идемпотентны, поэтому двойные вызовы (UI-кнопка «Сохранить» + этот синк) безопасны.
/// </summary>
public static class MailRuntimeSync
{
    private static readonly object Gate = new();
    private static MailSettings? _lastSeen;

    /// <summary>
    /// Базовый снимок (вызывается в MainViewModel после авто-старта монитора):
    /// фиксирует состояние на старте, без действий.
    /// </summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            _lastSeen = Clone(MailSettings.Get());
        }
    }

    /// <summary>
    /// Сверить runtime (монитор, кэш-соединения) с текущим почтовым снимком. UI-поток.
    /// </summary>
    public static void Sync()
    {
        MailSettings? previous;
        MailSettings current;
        lock (Gate)
        {
            current = MailSettings.Get();
            previous = _lastSeen;
            _lastSeen = Clone(current);
        }

        switch (MailRuntime.Decide(previous, current))
        {
            case MailRuntimeAction.Start:
                MailMonitor.TryStart();
                break;
            case MailRuntimeAction.Stop:
                MailMonitor.Stop();
                break;
            case MailRuntimeAction.Restart:
                MailMonitor.Stop();
                MailHolder.Reset();
                if (current.Enabled && current.IsConfigured)
                {
                    MailMonitor.TryStart();
                }
                break;
        }
    }

    /// <summary>Глубокая копия снимка (MailSettings — плоский POCO).</summary>
    private static MailSettings Clone(MailSettings s) => new()
    {
        Enabled = s.Enabled,
        ImapHost = s.ImapHost,
        ImapPort = s.ImapPort,
        SmtpHost = s.SmtpHost,
        SmtpPort = s.SmtpPort,
        PollIntervalSeconds = s.PollIntervalSeconds,
        WakePolicy = s.WakePolicy,
        WakeFolders = new List<string>(s.WakeFolders),
        WakeSenders = new List<string>(s.WakeSenders),
        OAuthClientId = s.OAuthClientId,
        OAuthClientSecret = s.OAuthClientSecret,
        RefreshToken = s.RefreshToken,
        Email = s.Email,
        Password = s.Password,
    };
}
