using System.Text.Json;
using QwenPlayground.Core.Security;

namespace QwenPlayground.Core.Tests;

public sealed class SecretRedactorTests
{
    [Fact]
    public void Redact_ReplacesRegisteredValue_InAnyText()
    {
        var secret = "reasoning-secret-" + Guid.NewGuid().ToString("N");
        SecretRedactor.Register(secret);

        var result = SecretRedactor.Redact($"thoughts before {secret} and after {secret}");

        Assert.Equal("thoughts before <redacted> and after <redacted>", result);
    }

    [Fact]
    public void Redact_ReplacesJsonEscapedValue_WithoutBreakingJson()
    {
        var secret = "ab\"\\secret-" + Guid.NewGuid().ToString("N");
        SecretRedactor.Register(secret);
        var json = JsonSerializer.Serialize(new { Text = $"reasoning {secret}" });

        var result = SecretRedactor.Redact(json);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("reasoning <redacted>", doc.RootElement.GetProperty("Text").GetString());
        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_LeavesUnknownTextUntouched()
    {
        var text = "ordinary text with a different-token-value";

        Assert.Equal(text, SecretRedactor.Redact(text));
    }
}
