using System.Text.Json;

namespace QwenPlayground.Core.Security;

/// <summary>Точная цензура зарегистрированных credentials для логов и персистентного transcript'а.</summary>
public static class SecretRedactor
{
    public const string Placeholder = "<redacted>";
    private const int MinSecretLength = 8;

    private static readonly object Gate = new();
    private static SecretVariants[] _secrets = [];

    private sealed record SecretVariants(string Raw, string JsonEscaped);

    public static void Register(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < MinSecretLength || value == Placeholder)
        {
            return;
        }
        lock (Gate)
        {
            if (_secrets.Any(secret => secret.Raw == value))
            {
                return;
            }
            var json = JsonSerializer.Serialize(value);
            var escaped = json.Length >= 2 ? json[1..^1] : value;
            _secrets = [.. _secrets, new SecretVariants(value, escaped)];
        }
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }
        SecretVariants[] secrets;
        lock (Gate)
        {
            secrets = _secrets;
        }
        foreach (var secret in secrets.OrderByDescending(candidate => candidate.Raw.Length))
        {
            text = text.Replace(secret.Raw, Placeholder, StringComparison.Ordinal);
            if (secret.JsonEscaped != secret.Raw)
            {
                text = text.Replace(secret.JsonEscaped, Placeholder, StringComparison.Ordinal);
            }
        }
        return text;
    }

    internal static void ClearForTests()
    {
        lock (Gate)
        {
            _secrets = [];
        }
    }
}
