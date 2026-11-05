using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Mail;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Tools;

/// <summary>
/// OAuth2 (Google) для desktop-приложения: loopback redirect + PKCE.
/// Тул открывает системный браузер, принимает callback на 127.0.0.1 и сохраняет refresh token.
/// </summary>
[Tool("mail_auth", "Authorize Google mail with OAuth2. Opens the system browser, listens for a loopback " +
    "callback on 127.0.0.1 and uses PKCE; no authorization code needs to be copied manually. " +
    "Requires OAuthClientId/OAuthClientSecret from a Google Cloud desktop client. Saves the refresh token " +
    "to settings.json (Mail section) and enables mail only after a successful exchange.",
    group: ToolGroup.Mail)]
public sealed class MailAuthTool : AgentTool
{
    [ToolParameter("OAuth client id (optional; saves to settings.json Mail section if provided)")]
    public string? ClientId { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var settings = MailSettings.Get();
        if (ClientId is not null)
        {
            MailSettings.Update(m => m.OAuthClientId = ClientId);
            settings = MailSettings.Get();
        }

        if (string.IsNullOrWhiteSpace(settings.OAuthClientId) || string.IsNullOrWhiteSpace(settings.OAuthClientSecret))
        {
            return "Error: OAuth client id/secret not set. Create a Google Cloud desktop client, enable Gmail API, " +
                   "enter the client id and secret in Settings → Mail, then call mail_auth.";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var redirect = $"http://127.0.0.1:{GetFreePort()}/";
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorizationUrl = BuildAuthorizationUrl(settings.OAuthClientId, redirect, state, challenge);

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        try
        {
            listener.Start();
            var callbackTask = listener.GetContextAsync();
            Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });

            var callback = await callbackTask.WaitAsync(timeout.Token);
            var query = callback.Request.QueryString;
            var error = query["error"];
            var code = query["code"];
            var returnedState = query["state"];
            if (error is not null || string.IsNullOrWhiteSpace(code) || returnedState != state)
            {
                var message = error is not null
                    ? $"Google authorization failed: {error}"
                    : "OAuth callback is invalid: missing code or state mismatch.";
                await WriteCallbackResponseAsync(callback.Response, false, message);
                return message;
            }
            await WriteCallbackResponseAsync(callback.Response, true, "Authorization received. You can return to QwenPlayground.");

            using var http = new HttpClient();
            using var response = await http.PostAsync("https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["client_id"] = settings.OAuthClientId,
                    ["client_secret"] = settings.OAuthClientSecret,
                    ["redirect_uri"] = redirect,
                    ["grant_type"] = "authorization_code",
                    ["code_verifier"] = verifier,
                }), timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return $"Token exchange failed ({(int)response.StatusCode}): {body}";
            }

            using var doc = JsonDocument.Parse(body);
            var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var tokenElement)
                ? tokenElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                return "Token exchange succeeded but returned no refresh_token. Mail was not enabled. " +
                       "Revoke the previous grant and retry so Google issues a new refresh token.";
            }

            var email = doc.RootElement.TryGetProperty("id_token", out var idToken)
                ? ExtractEmailFromJwt(idToken.GetString() ?? "")
                : "";
            MailSettings.Update(m =>
            {
                m.RefreshToken = refreshToken;
                if (!string.IsNullOrEmpty(email)) m.Email = email;
                m.Enabled = true;
            });
            MailMonitor.TryStart();
            return $"OAuth2 authorized. Refresh token saved. Email: {email}. Mail is configured and enabled.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return "mail_auth timed out after 5 minutes. No refresh token was saved.";
        }
        catch (Exception ex)
        {
            return $"mail_auth failed: {ex.Message}";
        }
    }

    private static string BuildAuthorizationUrl(string clientId, string redirect, string state, string challenge) =>
        "https://accounts.google.com/o/oauth2/v2/auth" +
        $"?client_id={Uri.EscapeDataString(clientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
        "&response_type=code" +
        $"&scope={Uri.EscapeDataString("openid email https://mail.google.com/")}" +
        "&access_type=offline" +
        "&prompt=consent" +
        $"&state={Uri.EscapeDataString(state)}" +
        "&code_challenge_method=S256" +
        $"&code_challenge={Uri.EscapeDataString(challenge)}";

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task WriteCallbackResponseAsync(HttpListenerResponse response, bool success, string message)
    {
        var html = "<!doctype html><meta charset=\"utf-8\"><title>QwenPlayground</title>" +
                   $"<body style=\"font-family:sans-serif;padding:2em\"><h2>{(success ? "Success" : "Authorization failed")}</h2>" +
                   $"<p>{WebUtility.HtmlEncode(message)}</p></body>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>Извлечь email из JWT (id_token) без верификации (только для display).</summary>
    private static string ExtractEmailFromJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return "";
            var payload = Encoding.UTF8.GetString(
                Convert.FromBase64String(parts[1].PadRight(parts[1].Length + (4 - parts[1].Length % 4) % 4, '=')));
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }
}
