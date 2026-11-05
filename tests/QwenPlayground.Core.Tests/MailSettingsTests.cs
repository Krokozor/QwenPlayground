using System.Text.Json;
using QwenPlayground.Core.Mail;

namespace QwenPlayground.Core.Tests;

/// <summary>Чистые тесты настроек: живой config/mail.json не читается и не изменяется.</summary>
public sealed class MailSettingsTests
{
    [Fact]
    public void Configured_False_When_Disabled()
    {
        var settings = new MailSettings { Enabled = false, RefreshToken = "token123" };
        Assert.False(settings.IsConfigured);
    }

    [Fact]
    public void Configured_True_When_Enabled_With_Token()
    {
        var settings = new MailSettings { Enabled = true, RefreshToken = "token123" };
        Assert.True(settings.IsConfigured);
    }

    [Fact]
    public void Configured_True_When_Enabled_With_Password()
    {
        var settings = new MailSettings { Enabled = true, Password = "pass123" };
        Assert.True(settings.IsConfigured);
    }

    [Fact]
    public void Configured_False_When_No_Token_And_No_Password()
    {
        var settings = new MailSettings { Enabled = true };
        Assert.False(settings.IsConfigured);
    }

    [Fact]
    public void WakePolicy_Default_Is_All()
    {
        Assert.Equal(MailWakePolicy.All, new MailSettings().WakePolicy);
    }

    [Fact]
    public void WakePolicy_RoundTrips_Through_Json()
    {
        var json = JsonSerializer.Serialize(new MailSettings { WakePolicy = MailWakePolicy.Senders });
        var restored = JsonSerializer.Deserialize<MailSettings>(json);
        Assert.NotNull(restored);
        Assert.Equal(MailWakePolicy.Senders, restored.WakePolicy);
    }
}
