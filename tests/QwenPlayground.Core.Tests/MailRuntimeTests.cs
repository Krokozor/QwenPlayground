using QwenPlayground.Core.Mail;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Runtime-синк почты после изменения настроек (план, раздел 15): чистая функция
/// MailRuntime.Decide. Покрытие: Enabled on/off, смена сервера (A→B), credentials,
/// «неопасные» поля (интервал/wake) не требуют рестарта, null-базовый снимок.
/// </summary>
public sealed class MailRuntimeTests
{
    /// <summary>Базовый настроенный и включённый снимок. Тесты мутуют инстансы напрямую.</summary>
    private static MailSettings Base() => new()
    {
        Enabled = true,
        Password = "pass-12345",
        ImapHost = "mail.a.com",
        ImapPort = 993,
        SmtpHost = "smtp.a.com",
        SmtpPort = 465,
        PollIntervalSeconds = 60,
    };

    [Fact]
    public void PreviousNull_IsBaseline_None()
    {
        Assert.Equal(MailRuntimeAction.None, MailRuntime.Decide(null, Base()));
    }

    [Fact]
    public void NoChange_None()
    {
        Assert.Equal(MailRuntimeAction.None, MailRuntime.Decide(Base(), Base()));
    }

    [Fact]
    public void EnabledOn_Start()
    {
        var previous = Base();
        previous.Enabled = false;
        var current = Base();

        Assert.Equal(MailRuntimeAction.Start, MailRuntime.Decide(previous, current));
    }

    [Fact]
    public void EnabledOff_Stop()
    {
        var previous = Base();
        var current = Base();
        current.Enabled = false;

        Assert.Equal(MailRuntimeAction.Stop, MailRuntime.Decide(previous, current));
    }

    [Fact]
    public void ImapHostAtoB_Restart()
    {
        var previous = Base();
        var current = Base();
        current.ImapHost = "mail.b.com";

        // Монитор и MailHolder держат кэш-соединения со старым сервером — полный рестарт.
        Assert.Equal(MailRuntimeAction.Restart, MailRuntime.Decide(previous, current));
    }

    [Fact]
    public void ImapPortChange_Restart()
    {
        var previous = Base();
        var current = Base();
        current.ImapPort = 143;

        Assert.Equal(MailRuntimeAction.Restart, MailRuntime.Decide(previous, current));
    }

    [Fact]
    public void CredentialChange_Restart()
    {
        var previous = Base();
        var current = Base();
        current.Password = "other-67890";

        Assert.Equal(MailRuntimeAction.Restart, MailRuntime.Decide(previous, current));
    }

    [Fact]
    public void RefreshTokenChange_Restart()
    {
        var previous = Base();
        previous.RefreshToken = "token-aaa";
        var current = Base();
        current.RefreshToken = "token-bbb";

        Assert.Equal(MailRuntimeAction.Restart, MailRuntime.Decide(previous, current));
    }

    [Fact]
    public void ConnectionChangedWhileDisabled_Stop()
    {
        var previous = Base();
        var current = Base();
        current.Enabled = false;
        current.ImapHost = "mail.b.com";

        Assert.Equal(MailRuntimeAction.Stop, MailRuntime.Decide(previous, current));
    }

    [Fact]
    public void PollIntervalOnly_None()
    {
        // Интервал читается монитором на каждом цикле — рестарт не нужен.
        var previous = Base();
        var current = Base();
        current.PollIntervalSeconds = 120;

        Assert.Equal(MailRuntimeAction.None, MailRuntime.Decide(previous, current));
    }

    [Fact]
    public void WakePolicyOnly_None()
    {
        var previous = Base();
        var current = Base();
        current.WakePolicy = MailWakePolicy.Senders;
        current.WakeSenders.Add("someone@example.com");

        Assert.Equal(MailRuntimeAction.None, MailRuntime.Decide(previous, current));
    }

    [Fact]
    public void EmailOnly_None()
    {
        var previous = Base();
        var current = Base();
        current.Email = "agent@example.com";

        Assert.Equal(MailRuntimeAction.None, MailRuntime.Decide(previous, current));
    }

    [Fact]
    public void NotConfiguredToConfigured_Restart()
    {
        // Enabled=true, но пароля нет → не настроено (монитор не работает). Пароль появился:
        // пароль — connection-поле, поэтому решение Restart (Stop-нооп + сброс holder'а + Start).
        var previous = Base();
        previous.Password = "";
        var current = Base();

        Assert.Equal(MailRuntimeAction.Restart, MailRuntime.Decide(previous, current));
    }
}
