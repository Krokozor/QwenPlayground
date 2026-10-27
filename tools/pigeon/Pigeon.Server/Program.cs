using System.Text.Json;
using Pigeon.Client;
using Pigeon.Server;

// pigeon — messenger for Qwens (docs/MESSANGER.md).
// Standalone server: survives harness rebuilds, single writer of the shared state,
// dumb by design (stores, routes, counts, hashes — all policy lives in the clients).
//
// Usage: pigeon [dataDir]   (default: ./data next to the exe)
// First run creates data/pigeon.config.json with a generated owner token and
// prints it — put it into the web UI (it is stored in the browser's localStorage).

var dataDir = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(AppContext.BaseDirectory, "data");
var config = ServerConfig.Load(dataDir);
var store = new Store(dataDir, config.HotThreshold);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); // console only, minimal
builder.WebHost.UseUrls($"http://{config.Bind}:{config.Port}");
// Web root = wwwroot next to the exe. .NET 10 forbids UseWebRoot after builder creation
// ("web root changed" crash), so the launcher starts the server with WorkingDirectory
// = exe dir (PigeonService.StartAsync), and the default web root lands on wwwroot.
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ── Logging ───────────────────────────────────────────────────────────────────────
// Минимальный request-лог (решение владельца, 2026-10-15: «копаешь глубоко, но надо
// логирование на сервере»): console + data/pigeon.log. Метод, путь, статус, кто (id).
var logPath = Path.Combine(dataDir, "pigeon.log");
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/mcp"))
    {
        await next();
        return;
    }
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        // Unhandled handler exceptions otherwise return a bare 500 with no trace —
        // log them (the request line below never runs in that case).
        Console.WriteLine($"pigeon: UNHANDLED {ctx.Request.Method} {ctx.Request.Path} -> {ex.Message}");
        throw;
    }
    var id = AuthId(ctx) ?? "-";
    var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {ctx.Request.Method} {ctx.Request.Path}{(ctx.Request.QueryString.HasValue ? ctx.Request.QueryString : "")} -> {ctx.Response.StatusCode} [{id}]";
    Console.WriteLine("pigeon: " + line);
    try
    {
        await File.AppendAllTextAsync(logPath, line + "\n");
    }
    catch
    {
        // лог не критичен
    }
});

// ── Auth ────────────────────────────────────────────────────────────────────────────

string? BearerToken(HttpContext ctx)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return null;
    var token = header["Bearer ".Length..].Trim();
    return token.Length == 0 ? null : token;
}

string? AuthId(HttpContext ctx)
{
    var token = BearerToken(ctx);
    if (token is null)
        return null;
    if (token == config.OwnerToken)
        return Store.OwnerId;
    return store.AuthByToken(token);
}

IResult Unauthorized() => Results.Unauthorized();
// Forbidden() needs AddAuthentication() registered — this server uses bearer
// tokens only, so return a plain 403 (Forbid threw "Unable to find IAuthenticationService").
IResult Forbidden() => Results.StatusCode(403);

// ── Identity ────────────────────────────────────────────────────────────────────────

app.MapPost("/api/join", (HttpContext ctx, PigeonJoinRequest req) =>
{
    var bearer = BearerToken(ctx);
    // The owner token is a master key, not a client credential: the owner seats people,
    // they don't join as themselves. Handing it to Join would read as "wrong token for
    // this id" and answer 401 (which is exactly the 403-adjacent bug the web UI hit).
    if (bearer == config.OwnerToken)
    {
        var (adminToken, adminDuplicate, _) = store.AdminJoin(req);
        return Results.Ok(new PigeonJoinResult { Token = adminToken, Duplicate = adminDuplicate });
    }
    // Re-join may authenticate via the Authorization header (PigeonClient sends it there),
    // not only via the body field.
    if (string.IsNullOrEmpty(req.Token))
        req.Token = bearer;
    var (token, duplicate, unauthorized) = store.Join(req);
    return unauthorized
        ? Results.Unauthorized()
        : Results.Ok(new PigeonJoinResult { Token = token, Duplicate = duplicate });
});

app.MapPost("/api/heartbeat", async (HttpContext ctx) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();

    // A heartbeat may carry a state: {"state":"generating","activity":"tool: browser_navigate"}.
    // A bare heartbeat (no body) is still a heartbeat — the state simply stays as it was.
    string? state = null, activity = null;
    if (ctx.Request.ContentLength is not null && ctx.Request.ContentLength > 0)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            if (doc.RootElement.TryGetProperty("state", out var s)) state = s.GetString();
            if (doc.RootElement.TryGetProperty("activity", out var a)) activity = a.GetString();
        }
        catch
        {
            // malformed body: presence still counts, the state is just not updated.
        }
    }

    store.Heartbeat(id, state, activity);
    return Results.NoContent();
});

app.MapGet("/api/who", (HttpContext ctx) =>
{
    if (AuthId(ctx) is null) return Unauthorized();
    return Results.Ok(store.Who());
});

// Read receipts for the war room: who in this channel has read up to where.
app.MapGet("/api/cursors", (HttpContext ctx, string channel) =>
{
    if (AuthId(ctx) is null) return Unauthorized();
    try
    {
        return Results.Ok(store.Cursors(channel));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapPost("/api/status", (HttpContext ctx, PigeonStatusSet req) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    store.SetStatus(id, req.Status ?? "");
    return Results.Ok(new { });
});

// ── Channels ────────────────────────────────────────────────────────────────────────

// Public (без auth): клиенту список каналов нужен ДО подключения (ввести адрес →
// увидеть каналы → выбрать), а токен есть только после join. LAN toy.
app.MapGet("/api/channels", () => Results.Ok(store.Channels()));

app.MapPost("/api/channels", (HttpContext ctx, PigeonChannelCreate req) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    if (id != Store.OwnerId) return Forbidden(); // Phase 0: the owner creates rooms
    return Results.Ok(store.CreateChannel(req.Name, req.Description ?? ""));
});

app.MapPost("/api/channels/{channelId}/describe", (HttpContext ctx, string channelId, PigeonChannelDescribe req) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    if (id != Store.OwnerId) return Forbidden(); // the owner defines what a room is for
    return store.DescribeChannel(channelId, req.Description)
        ? Results.Ok(store.Channels().First(c => c.Id == channelId))
        : Results.NotFound();
});

app.MapPost("/api/channels/{channelId}/rename", (HttpContext ctx, string channelId, PigeonChannelRename req) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    if (id != Store.OwnerId) return Forbidden();
    return store.RenameChannel(channelId, req.Name, req.Description)
        ? Results.Ok(store.Channels().First(c => c.Id == channelId))
        : Results.NotFound();
});

app.MapPost("/api/channels/{channelId}/archive", (HttpContext ctx, string channelId) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    if (id != Store.OwnerId) return Forbidden();
    store.ArchiveChannel(channelId);
    return Results.NoContent();
});

app.MapPost("/api/channels/{channelId}/delete", (HttpContext ctx, string channelId) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    if (id != Store.OwnerId) return Forbidden(); // the owner deletes rooms
    try
    {
        store.DeleteChannel(channelId);
        return Results.NoContent();
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

// Self-join: any authenticated participant seats itself (Phase 0: exactly one channel —
// joining a new one leaves the old). The owner's /api/membership stays for reassigning others.
app.MapPost("/api/channels/{channelId}/join", (HttpContext ctx, string channelId) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    try
    {
        store.Reassign(id, channelId);
        return Results.NoContent();
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapPost("/api/membership", (HttpContext ctx, PigeonMembership req) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    if (id != Store.OwnerId) return Forbidden(); // Phase 0: the owner seats the flock
    store.Reassign(req.Identity, req.Channel);
    return Results.NoContent();
});

// ── Messages ────────────────────────────────────────────────────────────────────────

app.MapGet("/api/unread", (HttpContext ctx) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    return Results.Ok(store.Unread(id));
});

app.MapPost("/api/messages", (HttpContext ctx, PigeonSendRequest req) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    try
    {
        return Results.Ok(store.Send(req, id));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (UnauthorizedAccessException)
    {
        return Forbidden();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// `wait` is in SECONDS on the wire (clamped 0..55).
app.MapGet("/api/messages", async (HttpContext ctx, string channel, long since, int wait) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    wait = Math.Clamp(wait, 0, 55);
    try
    {
        return Results.Ok(await store.Read(channel, since, wait * 1000, id));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapGet("/api/messages/peek", (HttpContext ctx, string channel, int count) =>
{
    if (AuthId(ctx) is null) return Unauthorized();
    count = Math.Clamp(count, 1, 500);
    try
    {
        return Results.Ok(store.Peek(channel, count));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapPost("/api/identity/{id}/forget", (HttpContext ctx, string id) =>
{
    if (AuthId(ctx) is not Store.OwnerId) return Unauthorized();
    return store.Forget(id) ? Results.NoContent() : Results.NotFound();
});

// ── Groups ──────────────────────────────────────────────────────────────────────────
// docs/MESSANGER.md §4/§9: an address that reaches a kind of agent. The owner curates the
// catalogue; members may join and leave of their own accord, except where the owner named
// them — that one is binding.

app.MapGet("/api/groups", (HttpContext ctx) =>
{
    if (AuthId(ctx) is null) return Unauthorized();
    return Results.Ok(store.Groups());
});

app.MapPost("/api/roles/join", (HttpContext ctx, PigeonRoleSelf req) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    var error = store.JoinGroup(id, req.Group, voluntary: true);
    return error is null ? Results.NoContent() : Results.BadRequest(error);
});

app.MapPost("/api/roles/leave", (HttpContext ctx, PigeonRoleSelf req) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    var error = store.LeaveGroup(id, req.Group);
    return error is null ? Results.NoContent() : Results.BadRequest(error);
});

app.MapPost("/api/groups", (HttpContext ctx, PigeonGroupCreate req) =>
{
    if (AuthId(ctx) is not Store.OwnerId) return Unauthorized();
    return store.CreateGroup(req.Name, req.Description ?? "") ? Results.Ok() : Results.Conflict();
});

app.MapPost("/api/groups/{groupId}/delete", (HttpContext ctx, string groupId) =>
{
    if (AuthId(ctx) is not Store.OwnerId) return Unauthorized();
    return store.DeleteGroup(groupId) ? Results.Ok() : Results.NotFound();
});

// ── File pool (path-addressed, mutable; docs/MESSANGER.md §7) ─────────────────────

app.MapGet("/api/files", (HttpContext ctx) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    try
    {
        // Listing is addressed by ?path= (the bare /api/files/{**path} route returns file
        // content, so a directory needs its own query form). Without reading the query the
        // endpoint always listed the root — the client's FilesLsAsync for a subdirectory
        // silently showed the wrong directory.
        return Results.Ok(store.FileLs(ctx.Request.Query["path"].ToString()));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
});

app.MapGet("/api/files/{**path}", (HttpContext ctx, string path) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    try
    {
        var (data, isText) = store.FileGet(path);
        if (isText)
        {
            // The UI must know whether the body is the JSON text wrapper or raw bytes:
            // a .json file is text (wrapped), a .gltf is binary-by-extension but also JSON.
            ctx.Response.Headers["X-Pigeon-Text"] = "1";
            var meta = store.FileLs(path).FirstOrDefault();
            return Results.Ok(new PigeonFileContent
            {
                Path = path,
                Content = System.Text.Encoding.UTF8.GetString(data),
                Size = data.Length,
                Owner = meta?.Owner ?? "",
                Hash = meta?.Hash ?? "",
            });
        }
        // Binary: raw bytes with content type.
        ctx.Response.Headers["X-Pigeon-Text"] = "0";
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var ct = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".glb" => "model/gltf-binary",
            ".gltf" or ".json" => "application/json",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
        return Results.File(data, ct);
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
});

app.MapPut("/api/files/{**path}", async (HttpContext ctx, string path) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    var contentType = ctx.Request.ContentType ?? "";
    byte[] data;
    if (contentType.Contains("application/json"))
    {
        // ReadFromJsonAsync uses the app's JSON options (camelCase, case-insensitive).
        // A manual Deserialize with default options leaves Content null for {"content":...},
        // and GetBytes(null) used to turn the request into a 500.
        var req = await ctx.Request.ReadFromJsonAsync<PigeonFilePut>();
        if (req is null) return Results.BadRequest("missing body");
        data = System.Text.Encoding.UTF8.GetBytes(req.Content ?? "");
    }
    else
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        data = ms.ToArray();
    }
    try
    {
        var result = store.FilePut(path, data, id);
        return Results.Ok(result);
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
});

app.MapPatch("/api/files/{**path}", (HttpContext ctx, string path, PigeonFileEdit req) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    try
    {
        return store.FileEdit(path, req.OldText ?? "", req.NewText ?? "")
            ? Results.Ok(new { path, edited = true })
            : Results.Conflict("old_text not found exactly once");
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
});

app.MapDelete("/api/files/{**path}", (HttpContext ctx, string path) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    try
    {
        store.FileDelete(path);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
});

app.MapPost("/api/groups/{groupId}/assign", (HttpContext ctx, string groupId, PigeonGroupMember req) =>
{
    if (AuthId(ctx) is not Store.OwnerId) return Unauthorized();
    if (!store.Exists(req.Identity)) return Results.NotFound();
    store.AssignRole(req.Identity, groupId);
    return Results.NoContent();
});

app.MapPost("/api/groups/{groupId}/unassign", (HttpContext ctx, string groupId, PigeonGroupMember req) =>
{
    if (AuthId(ctx) is not Store.OwnerId) return Unauthorized();
    store.UnassignRole(req.Identity, groupId);
    return Results.NoContent();
});

// ── MCP adapter ──────────────────────────────────────────────────────────────────────
// docs/MESSANGER.md §14.1: the same participant surface, as MCP tools over JSON-RPC. Mounted
// last so it never shadows /api. See Mcp.cs for the protocol-revision note.

Mcp.Map(app, store, config, AuthId);

Console.WriteLine($"pigeon: listening on http://{config.Bind}:{config.Port} (data: {dataDir})");
Console.WriteLine($"pigeon: owner token: {config.OwnerToken}");
Console.WriteLine($"pigeon: MCP at http://{config.Bind}:{config.Port}/mcp (auto-join: {(config.McpAutoJoin ? "on" : "off")})");
app.Run();

// ── Request payloads (web-shape) ────────────────────────────────────────────────────

public sealed record PigeonStatusSet(string? Status);
public sealed record PigeonChannelCreate(string Name, string? Description);
public sealed record PigeonMembership(string Identity, string Channel);
public sealed record PigeonGroupCreate(string Name, string? Description);
public sealed record PigeonGroupMember(string Identity);
public sealed record PigeonRoleSelf(string Group);

/// <summary>
/// Server config: data/pigeon.config.json. Created on first run with a generated
/// owner token (printed to the console).
/// </summary>
public sealed class ServerConfig
{
    public int Port { get; set; } = 8199;
    /// <summary>
    /// Bind address. Default 127.0.0.1 (local only — the server is a LAN toy, not a
    /// public endpoint). Set to 0.0.0.0 to expose on all interfaces (cross-machine,
    /// Phase 3).
    /// </summary>
    public string Bind { get; set; } = "127.0.0.1";
    public string OwnerToken { get; set; } = "";
    /// <summary>Consecutive agent messages in a thread before it is tagged hot. 0 = off (default).</summary>
    public int HotThreshold { get; set; } = 0;
    /// <summary>
    /// MCP (docs/MESSANGER.md §14.1): let a foreign client that presents no bearer token
    /// seat itself on <c>initialize</c> as <c>mcp:&lt;client-name&gt;</c>. Turn off to require
    /// a real token on every MCP call.
    /// </summary>
    public bool McpAutoJoin { get; set; } = true;

    public static ServerConfig Load(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "pigeon.config.json");
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        if (File.Exists(path))
        {
            var cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), options) ?? new ServerConfig();
            if (string.IsNullOrWhiteSpace(cfg.OwnerToken))
            {
                cfg.OwnerToken = Guid.NewGuid().ToString("N");
                File.WriteAllText(path, JsonSerializer.Serialize(cfg, options));
            }
            return cfg;
        }
        var fresh = new ServerConfig { OwnerToken = Guid.NewGuid().ToString("N") };
        File.WriteAllText(path, JsonSerializer.Serialize(fresh, options));
        return fresh;
    }
}
