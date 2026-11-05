using System.Text.Json.Serialization;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Mail;

/// <summary>
/// Политика пробуждения от входящих писем (решение владельца, 2026-10-29):
/// настраиваемая политика «что будит агента», остальное — просто счётчик в state-блоке.
/// </summary>
public enum MailWakePolicy
{
    /// <summary>Будят все входящие письма (любая папка, любой отправитель).</summary>
    All = 0,

    /// <summary>Будят письма только из папок <see cref="MailSettings.WakeFolders"/> (пусто = никто не будит).</summary>
    Folders = 1,

    /// <summary>Будят письма только от отправителей из <see cref="MailSettings.WakeSenders"/> (пусто = никто не будит).</summary>
    Senders = 2,
}

/// <summary>
/// Настройки почтового клиента (IMAP/SMTP). POCO-группа внутри AppSettings
/// (план, раздел 14: config/mail.json слит в settings.json). Статические методы —
/// тонкий шим над AppSettings.Mail: mail-подсистема (MailService/MailMonitor/MailTools,
/// UI) не знает, где живёт объект. Секреты ([SettingSecret]) маскируются в get_settings
/// и регистрируются в SecretRedactor автоматически (план, раздел 13).
/// </summary>
public sealed class MailSettings
{
    /// <summary>Живой объект почты (AppSettings.Mail).</summary>
    public static MailSettings Get() => AppSettings.Get().Mail;

    /// <summary>Атомарно сохранить (вместе со всем AppSettings).</summary>
    public static void Save() => AppSettings.Save();

    /// <summary>Write-through: мутация + запись атомарно (тулы агента, UI).</summary>
    public static void Update(Action<MailSettings> mutate)
        => AppSettings.Update(a => mutate(a.Mail));

    /// <summary>
    /// Почта включена И OAuth-токен есть — единый источник правды для опциональности фичи.
    /// Выкл/нет токена = тулы отвечают «почта не настроена», state-строки нет, polling не идёт.
    /// </summary>
    public static bool Configured => Get().IsConfigured;

    /// <summary>Вычислимое состояние конкретного экземпляра — удобно для изолированных тестов.
    /// [JsonIgnore]: это производное состояние, не поле — не сериализуем (иначе в файл
    /// попадало бы статическое «snapshot»-значение, вводящее в заблуждение).</summary>
    [JsonIgnore]
    public bool IsConfigured =>
        Enabled &&
        (!string.IsNullOrWhiteSpace(RefreshToken) || !string.IsNullOrWhiteSpace(Password));

    /// <summary>Мастер-тумблер почты. Выкл — polling не идёт, state-строки нет.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>IMAP-хост (Gmail: imap.gmail.com).</summary>
    public string ImapHost { get; set; } = "imap.gmail.com";

    /// <summary>IMAP-порт (Gmail: 993, SSL).</summary>
    public int ImapPort { get; set; } = 993;

    /// <summary>SMTP-хост (Gmail: smtp.gmail.com).</summary>
    public string SmtpHost { get; set; } = "smtp.gmail.com";

    /// <summary>SMTP-порт (Gmail: 465, SSL).</summary>
    public int SmtpPort { get; set; } = 465;

    /// <summary>Политика пробуждения от входящих (см. MailWakePolicy).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MailWakePolicy WakePolicy { get; set; } = MailWakePolicy.All;

    /// <summary>
    /// Папки для политики WakePolicy=Folders (имена IMAP-папок, напр. "INBOX", "INBOX/Work").
    /// Пусто при Folders = никто не будит (только счётчик в state).
    /// </summary>
    public List<string> WakeFolders { get; set; } = new();

    /// <summary>
    /// Белый список отправителей для политики WakePolicy=Senders (email-адреса, напр. "owner@example.com").
    /// Пусто при Senders = никто не будит.
    /// </summary>
    public List<string> WakeSenders { get; set; } = new();

    // ── OAuth2 (Google) ─────────────────────────────────────────────────────────────

    /// <summary>OAuth client id (Google Cloud → APIs &amp; Services → Credentials). Пусто — OAuth не настроен.</summary>
    public string OAuthClientId { get; set; } = string.Empty;

    /// <summary>OAuth client secret.</summary>
    [SettingSecret]
    public string OAuthClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Refresh token (получается через mail_auth: owner открывает URL, даёт код,
    /// агент обменивает на refresh token). Пусто — не авторизован.
    /// НЕ логировать, НЕ цитировать в ответах (правило secrets.md).
    /// </summary>
    [SettingSecret]
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Email аккаунта (для display и SMTP FROM). Заполняется при OAuth-авторизации.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Пароль для PLAIN-аутентификации (не-Google провайдеры). У Google PLAIN блокируется —
    /// использовать OAuth2 (RefreshToken). Пусто = PLAIN не используется.
    /// </summary>
    [SettingSecret]
    public string Password { get; set; } = string.Empty;

    /// <summary>Интервал polling непрочитанных (сек). Минимум 10. Дефолт 60.</summary>
    public int PollIntervalSeconds { get; set; } = 60;
}
