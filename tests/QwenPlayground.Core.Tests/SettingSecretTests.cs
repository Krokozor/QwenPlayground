using System.Reflection;
using System.Text.Json;
using QwenPlayground.Core.Mail;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Security;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Tools;
using QwenPlayground.Core.Tools.Builtins;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Изолированный тип настроек для тестов [SettingSecret] (план, раздел 13):
/// свой файл под tests/, реальные конфиги не трогает.
/// </summary>
[SettingsFile("tests/setting-secret-probe.json")]
internal sealed class SecretProbeSettings
{
    /// <summary>Секрет с пустым дефолтом.</summary>
    [SettingSecret]
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>Секрет с НЕПУСТЫМ дефолтом: дефолтное значение не должно регистрироваться.</summary>
    [SettingSecret]
    public string DefaultedToken { get; set; } = "default-token-xyz-999";

    /// <summary>Обычное поле — не секрет.</summary>
    public string Plain { get; set; } = "plain-value-not-a-secret";
}

/// <summary>
/// Модель секретов в настройках (план, раздел 13): авто-регистрация в SecretRedactor
/// через SettingsStore + агентское форматирование (маскировка get_settings,
/// безэховый set_setting).
/// </summary>
public sealed class SettingSecretTests
{
    private static string ProbePath()
        => Path.Combine(SelfBuildPaths.WorkspaceRoot, "tests", "setting-secret-probe.json");

    private static void ResetProbeFile()
    {
        var path = ProbePath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Update_SecretValue_IsAutoRegisteredInRedactor()
    {
        ResetProbeFile();
        try
        {
            SettingsStore<SecretProbeSettings>.Reload();
            const string secret = "super-secret-token-abc123";

            SettingsStore<SecretProbeSettings>.Update(s => s.ApiToken = secret);

            // Значение зарегистрировано: Redactor заменяет его на плейсхолдер.
            Assert.Equal($"x {SecretRedactor.Placeholder} y", SecretRedactor.Redact($"x {secret} y"));
            // Обычное поле не регистрируется.
            Assert.Equal("plain-value-not-a-secret", SecretRedactor.Redact("plain-value-not-a-secret"));
        }
        finally
        {
            ResetProbeFile();
        }
    }

    [Fact]
    public void LoadFromDisk_RegistersSecret()
    {
        ResetProbeFile();
        const string secret = "loaded-secret-token-xyz789";
        try
        {
            File.WriteAllText(ProbePath(), JsonSerializer.Serialize(new SecretProbeSettings { ApiToken = secret }));

            SettingsStore<SecretProbeSettings>.Reload();

            Assert.Equal($"x {SecretRedactor.Placeholder} y", SecretRedactor.Redact($"x {secret} y"));
        }
        finally
        {
            ResetProbeFile();
        }
    }

    [Fact]
    public void Update_ValueEqualToDefault_IsNotRegistered()
    {
        ResetProbeFile();
        try
        {
            SettingsStore<SecretProbeSettings>.Reload();

            // Значение равно дефолту типа — не регистрируем (иначе цензурировали бы
            // случайные куски обычного текста, совпадающие с дефолтом).
            SettingsStore<SecretProbeSettings>.Update(s => s.DefaultedToken = "default-token-xyz-999");

            Assert.Equal("default-token-xyz-999", SecretRedactor.Redact("default-token-xyz-999"));
        }
        finally
        {
            ResetProbeFile();
        }
    }

    [Fact]
    public void Update_ShortValue_IsNotRegistered()
    {
        ResetProbeFile();
        try
        {
            SettingsStore<SecretProbeSettings>.Reload();

            // Короткие значения (<8) пропускает сам Redactor.
            SettingsStore<SecretProbeSettings>.Update(s => s.ApiToken = "short1");

            Assert.Equal("short1", SecretRedactor.Redact("short1"));
        }
        finally
        {
            ResetProbeFile();
        }
    }

    [Fact]
    public void Display_FormatSecret_ShowsMarker_NotValue()
    {
        var prop = typeof(SecretProbeSettings).GetProperty(nameof(SecretProbeSettings.ApiToken))!;

        Assert.Equal("<hidden — empty>", SettingDisplay.FormatSecret(null));
        Assert.Equal("<hidden — empty>", SettingDisplay.FormatSecret(""));
        Assert.Equal("<hidden — set, 12 chars>", SettingDisplay.FormatSecret("123456789012"));

        // Через Format: секрет — маркером, даже если значение заполнено.
        var settings = new SecretProbeSettings { ApiToken = "some-secret-value-999" };
        Assert.Equal("<hidden — set, 21 chars>", SettingDisplay.Format(prop, settings.ApiToken));
    }

    [Fact]
    public void Display_Format_PlainProperty_ShowsValue()
    {
        var prop = typeof(SecretProbeSettings).GetProperty(nameof(SecretProbeSettings.Plain))!;
        Assert.Equal("plain-value-not-a-secret", SettingDisplay.Format(prop, "plain-value-not-a-secret"));
        Assert.False(SettingDisplay.IsSecret(prop));
        Assert.True(SettingDisplay.IsSecret(
            typeof(SecretProbeSettings).GetProperty(nameof(SecretProbeSettings.ApiToken))!));
    }

    [Fact]
    public void Display_ChangeMessage_Secret_NoEcho()
    {
        const string secret = "echo-test-secret-4242";

        var secretMessage = SettingDisplay.ChangeMessage("ApiToken", isSecret: true, "old-secret", secret);
        Assert.DoesNotContain(secret, secretMessage);
        Assert.DoesNotContain("old-secret", secretMessage);
        Assert.Contains("<redacted> → <redacted>", secretMessage);
        Assert.Contains("Persisted", secretMessage);

        var plainMessage = SettingDisplay.ChangeMessage("Plain", isSecret: false, "a", "b");
        Assert.Contains("= a → b", plainMessage);
    }

    // ── Вложенные группы (план, раздел 14): AppSettings.Mail ─────────────────────────

    [Fact]
    public void AppSettings_RoundTrips_NestedMail()
    {
        var settings = new AppSettings
        {
            Mail = new MailSettings { Enabled = true, ImapHost = "mail.example.com", PollIntervalSeconds = 120 }
        };

        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        Assert.True(restored.Mail.Enabled);
        Assert.Equal("mail.example.com", restored.Mail.ImapHost);
        Assert.Equal(120, restored.Mail.PollIntervalSeconds);
    }

    [Fact]
    public void AppSettings_MissingMailSection_Defaults()
    {
        // Старый settings.json без секции Mail — десериализация не роняет и даёт дефолты.
        var restored = JsonSerializer.Deserialize<AppSettings>("{}")!;

        Assert.NotNull(restored.Mail);
        Assert.False(restored.Mail.Enabled);
        Assert.Equal(60, restored.Mail.PollIntervalSeconds);
    }

    [Fact]
    public void Update_NestedSecret_IsAutoRegistered()
    {
        try
        {
            const string secret = "nested-mail-secret-777";
            AppSettings.Update(a => a.Mail.Password = secret);

            Assert.Equal($"x {SecretRedactor.Placeholder} y", SecretRedactor.Redact($"x {secret} y"));
        }
        finally
        {
            AppSettings.Update(a => a.Mail.Password = string.Empty);
        }
    }

    [Fact]
    public async Task GetSettings_PrintsNestedMailSection_WithMaskedSecret()
    {
        var result = await new GetSettingTool().ExecuteAsync(
            new ToolContext(SelfBuildPaths.WorkspaceRoot), CancellationToken.None);

        Assert.Contains("- Mail (MailSettings):", result);
        // Секрет не печатается со значением — только маркером.
        var passwordLine = result.Split('\n').First(l => l.Contains("- Password (String):"));
        Assert.Contains("<hidden", passwordLine);
        var passwordValue = AppSettings.Get().Mail.Password;
        if (!string.IsNullOrEmpty(passwordValue))
        {
            Assert.DoesNotContain(passwordValue, result);
        }
    }

    [Fact]
    public async Task SetSetting_DottedPath_SetsNestedValue()
    {
        var original = AppSettings.Get().Mail.PollIntervalSeconds;
        try
        {
            var result = await new SetSettingTool
            {
                Name = "Mail.PollIntervalSeconds",
                Value = (original + 1).ToString()
            }.ExecuteAsync(new ToolContext(SelfBuildPaths.WorkspaceRoot), CancellationToken.None);

            Assert.Contains("Persisted", result);
            Assert.Equal(original + 1, AppSettings.Get().Mail.PollIntervalSeconds);
        }
        finally
        {
            AppSettings.Get().Mail.PollIntervalSeconds = original;
            AppSettings.Save();
        }
    }

    [Fact]
    public async Task SetSetting_DottedPath_Secret_NoEcho()
    {
        try
        {
            const string secret = "dotted-path-secret-31337";
            var result = await new SetSettingTool
            {
                Name = "Mail.Password",
                Value = secret
            }.ExecuteAsync(new ToolContext(SelfBuildPaths.WorkspaceRoot), CancellationToken.None);

            Assert.DoesNotContain(secret, result);
            Assert.Contains("<redacted> → <redacted>", result);
            Assert.Equal(secret, AppSettings.Get().Mail.Password);
        }
        finally
        {
            AppSettings.Get().Mail.Password = string.Empty;
            AppSettings.Save();
        }
    }

    [Fact]
    public async Task SetSetting_BadPath_FriendlyError()
    {
        var result = await new SetSettingTool
        {
            Name = "Mail.NoSuchProperty",
            Value = "x"
        }.ExecuteAsync(new ToolContext(SelfBuildPaths.WorkspaceRoot), CancellationToken.None);

        Assert.Contains("not found", result);
        Assert.Contains("Mail.Password", result); // подсказка про точечные пути
    }
}
