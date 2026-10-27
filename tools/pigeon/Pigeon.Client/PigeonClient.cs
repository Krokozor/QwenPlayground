using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pigeon.Client;

/// <summary>
/// Pigeon server client (docs/MESSANGER.md §13): pure transport — HTTP, envelope,
/// cursors, long-poll. Zero harness assumptions: the per-harness glue (tools, the
/// &lt;state&gt; line, wake) sits on top of this. Portable to any harness in the family.
/// </summary>
public sealed class PigeonClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public string BaseUrl { get; }
    public string? Token { get; private set; }

    public PigeonClient(string baseUrl, HttpClient? http = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = http ?? new HttpClient();
        _ownsHttp = http is null;
    }

    public void SetToken(string? token) => Token = token;

    private HttpRequestMessage Req(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        if (Token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return req;
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient http, HttpRequestMessage req, CancellationToken ct = default)
    {
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    private async Task<T> PostJsonAsync<T>(HttpRequestMessage req, object? payload, CancellationToken ct = default)
    {
        if (payload is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        return body.Length == 0 ? default! : JsonSerializer.Deserialize<T>(body, Json)!;
    }

    /// <summary>Join (or re-join) the server. Idempotent: a persisted token re-authenticates.</summary>
    public Task<PigeonJoinResult> JoinAsync(PigeonJoinRequest req, CancellationToken ct = default)
        => PostJsonAsync<PigeonJoinResult>(Req(HttpMethod.Post, "/api/join"), req);

    /// <summary>All participants: identity cards + presence.</summary>
    public Task<PigeonIdentity[]> WhoAsync(CancellationToken ct = default)
        => GetJsonAsync<PigeonIdentity[]>(_http, Req(HttpMethod.Get, "/api/who"));

    /// <summary>All channels (meta).</summary>
    public Task<PigeonChannel[]> ChannelsAsync(CancellationToken ct = default)
        => GetJsonAsync<PigeonChannel[]>(_http, Req(HttpMethod.Get, "/api/channels"));

    /// <summary>My unread info per channel (cursor + count).</summary>
    public Task<PigeonUnread[]> UnreadAsync(CancellationToken ct = default)
        => GetJsonAsync<PigeonUnread[]>(_http, Req(HttpMethod.Get, "/api/unread"));

    /// <summary>The group catalogue: what an @mention of a name would reach.</summary>
    public Task<PigeonGroup[]> GroupsAsync(CancellationToken ct = default)
        => GetJsonAsync<PigeonGroup[]>(_http, Req(HttpMethod.Get, "/api/groups"));

    /// <summary>Join a group of your own accord. Owner-assigned groups refuse.</summary>
    public Task JoinGroupAsync(string group, CancellationToken ct = default)
        => PostJsonAsync<PigeonMessage>(Req(HttpMethod.Post, "/api/roles/join"), new PigeonRoleChange { Group = group });

    /// <summary>Leave a group you joined yourself.</summary>
    public Task LeaveGroupAsync(string group, CancellationToken ct = default)
        => PostJsonAsync<PigeonMessage>(Req(HttpMethod.Post, "/api/roles/leave"), new PigeonRoleChange { Group = group });

    /// <summary>Send a message; returns the stored envelope (with the assigned id).</summary>
    public Task<PigeonMessage> SendAsync(PigeonSendRequest req, CancellationToken ct = default)
        => PostJsonAsync<PigeonMessage>(Req(HttpMethod.Post, "/api/messages"), req);

    /// <summary>
    /// Long-poll read of my channel: returns new messages after <paramref name="since"/>
    /// (advancing my server-side cursor) or waits up to <paramref name="waitSeconds"/> for some.
    /// </summary>
    public Task<PigeonMessage[]> ReadAsync(string channel, long since, int waitSeconds = 25, CancellationToken ct = default)
        => GetJsonAsync<PigeonMessage[]>(_http,
            Req(HttpMethod.Get, $"/api/messages?channel={Uri.EscapeDataString(channel)}&since={since}&wait={waitSeconds}"));

    /// <summary>Peek the last N messages of a channel without advancing my cursor.</summary>
    public Task<PigeonMessage[]> PeekAsync(string channel, int count = 50, CancellationToken ct = default)
        => GetJsonAsync<PigeonMessage[]>(_http,
            Req(HttpMethod.Get, $"/api/messages/peek?channel={Uri.EscapeDataString(channel)}&count={count}"));

    /// <summary>Presence heartbeat (online = last heartbeat &lt; 45 s).</summary>
    public Task HeartbeatAsync(CancellationToken ct = default)
        => PostJsonAsync<JsonElement>(Req(HttpMethod.Post, "/api/heartbeat"), null);

    /// <summary>
    /// Heartbeat that also says what the participant is doing: idle | generating | dispatching |
    /// hung | errored, plus one line of context. Presence without state only says "a process
    /// exists"; the state is what makes the roster worth reading.
    /// </summary>
    public Task HeartbeatAsync(string state, string activity = "", CancellationToken ct = default)
        => PostJsonAsync<JsonElement>(Req(HttpMethod.Post, "/api/heartbeat"),
            new { state, activity });

    /// <summary>Update my public status line ("what I'm doing").</summary>
    public Task SetStatusAsync(string status, CancellationToken ct = default)
        => PostJsonAsync<JsonElement>(Req(HttpMethod.Post, "/api/status"), new { status });

    /// <summary>
    /// Join a channel (self-join: leaves the current one — Phase 0 limit of exactly one channel).
    /// </summary>
    public Task JoinChannelAsync(string channelId, CancellationToken ct = default)
        => PostJsonAsync<JsonElement>(Req(HttpMethod.Post, $"/api/channels/{Uri.EscapeDataString(channelId)}/join"), null);

    // ── Owner (web UI) ──────────────────────────────────────────────────────────────

    public Task<PigeonChannel> CreateChannelAsync(string name, string description, CancellationToken ct = default)
        => PostJsonAsync<PigeonChannel>(Req(HttpMethod.Post, "/api/channels"), new { name, description });

    public Task ArchiveChannelAsync(string channelId, CancellationToken ct = default)
        => PostJsonAsync<JsonElement>(Req(HttpMethod.Post, $"/api/channels/{Uri.EscapeDataString(channelId)}/archive"), null);

    /// <summary>Delete a channel (owner only): log, meta and membership are removed.</summary>
    public Task DeleteChannelAsync(string channelId, CancellationToken ct = default)
        => PostJsonAsync<JsonElement>(Req(HttpMethod.Post, $"/api/channels/{Uri.EscapeDataString(channelId)}/delete"), null);

    /// <summary>Reassign a participant to a channel (leaves the old one — Phase 0 limit).</summary>
    public Task ReassignAsync(string identityId, string channelId, CancellationToken ct = default)
        => PostJsonAsync<JsonElement>(Req(HttpMethod.Post, "/api/membership"), new { identity = identityId, channel = channelId });

    // ── File pool ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encode a pool-relative path segment by segment. Encoding the whole path with
    /// Uri.EscapeDataString turns '/' into %2F, and Kestrel does not decode an encoded
    /// slash back into the {**path} route value — every nested path came back 404.
    /// Per-segment encoding keeps the separators real and only escapes the names.
    /// </summary>
    private static string EncodePath(string path) =>
        string.Join("/", path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

    public Task<PigeonFileEntry[]> FilesLsAsync(string path, CancellationToken ct = default)
        => GetJsonAsync<PigeonFileEntry[]>(_http, Req(HttpMethod.Get, $"/api/files?path={Uri.EscapeDataString(path)}"), ct);

    public Task<PigeonFileContent> FileReadAsync(string path, CancellationToken ct = default)
        => GetJsonAsync<PigeonFileContent>(_http, Req(HttpMethod.Get, $"/api/files/{EncodePath(path)}"), ct);

    public async Task<byte[]> FileDownloadAsync(string path, CancellationToken ct = default)
    {
        using var req = Req(HttpMethod.Get, $"/api/files/{EncodePath(path)}");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public Task<PigeonFileResult> FileWriteAsync(string path, string content, CancellationToken ct = default)
        => PostJsonAsync<PigeonFileResult>(Req(HttpMethod.Put, $"/api/files/{EncodePath(path)}"), new PigeonFilePut { Content = content }, ct);

    public async Task<PigeonFileResult> FileUploadAsync(string path, byte[] data, CancellationToken ct = default)
    {
        var req = Req(HttpMethod.Put, $"/api/files/{EncodePath(path)}");
        req.Content = new ByteArrayContent(data);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        return body.Length == 0 ? default! : JsonSerializer.Deserialize<PigeonFileResult>(body, Json)!;
    }

    public async Task<bool> FileEditAsync(string path, string oldText, string newText, CancellationToken ct = default)
    {
        var req = Req(new HttpMethod("PATCH"), $"/api/files/{EncodePath(path)}");
        req.Content = new StringContent(JsonSerializer.Serialize(new PigeonFileEdit { OldText = oldText, NewText = newText }, Json), System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        return resp.StatusCode == System.Net.HttpStatusCode.OK;
    }

    public Task FileDeleteAsync(string path, CancellationToken ct = default)
        => SendNoContentAsync(Req(HttpMethod.Delete, $"/api/files/{EncodePath(path)}"), ct);

    private async Task SendNoContentAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
