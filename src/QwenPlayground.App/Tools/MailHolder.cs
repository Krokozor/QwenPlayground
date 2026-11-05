using MailKit;
using QwenPlayground.Core.Mail;
using MailService = QwenPlayground.Core.Mail.MailService;

namespace QwenPlayground.App.Tools;

/// <summary>
/// Статический holder почтового сервиса (паттерн PigeonSession/McpService): один MailService
/// на процесс, ленивое подключение при первом обращении. Тулы полки "mail" работают через
/// GetOrConnectAsync. Per-session состояние (курсоры) — в MailSession (фаза 2).
/// </summary>
public static class MailHolder
{
    private static MailService? _service;
    private static readonly object Gate = new();

    /// <summary>Живой сервис (null, если не подключался).</summary>
    public static MailService? Service { get { lock (Gate) return _service; } }

    /// <summary>
    /// Получить сервис, подключив при первом обращении. Ошибка — если почта не настроена
    /// (нет refresh token и нет пароля) или соединение упало.
    /// </summary>
    public static async Task<MailService> GetOrConnectAsync(CancellationToken ct)
    {
        lock (Gate)
        {
            if (_service is { IsConnected: true })
            {
                return _service;
            }
        }
        if (!MailSettings.Configured)
        {
            throw new InvalidOperationException(
                "Mail not configured: set mail settings (OAuth2 refresh token or password) " +
                "and Enabled=true. Use mail_auth for OAuth2 setup.");
        }
        var service = new MailService();
        await service.ConnectAsync(ct);
        lock (Gate)
        {
            var old = _service;
            _service = service;
            if (old is not null)
            {
                _ = old.DisposeAsync();
            }
        }
        return service;
    }

    /// <summary>
    /// Сбросить кэшированный сервис (смена connection-настроек: план, раздел 15).
    /// Следующий GetOrConnectAsync подключится заново по новым настройкам.
    /// </summary>
    public static void Reset()
    {
        MailService? old;
        lock (Gate)
        {
            old = _service;
            _service = null;
        }
        if (old is not null)
        {
            _ = old.DisposeAsync();
        }
    }

    /// <summary>Разобрать UniqueId из строки ("uid" или "uid:validity").</summary>
    public static UniqueId ParseUid(string s) => UniqueId.Parse(s);

    /// <summary>UniqueId → строка (для тулов).</summary>
    public static string UidToString(UniqueId uid) => uid.ToString();
}
