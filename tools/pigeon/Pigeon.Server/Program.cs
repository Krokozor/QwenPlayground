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
builder.WebHost.UseUrls($"http://0.0.0.0:{config.Port}");
// Pin the web root to the build output explicitly: the default resolves against the
// CURRENT directory, and the server is usually started from somewhere else (cwd ≠ exe dir).
builder.WebHost.UseWebRoot(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

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

// ── Identity ────────────────────────────────────────────────────────────────────────

app.MapPost("/api/join", (HttpContext ctx, PigeonJoinRequest req) =>
{
    // Re-join may authenticate via the Authorization header (PigeonClient sends it there),
    // not only via the body field.
    if (string.IsNullOrEmpty(req.Token))
        req.Token = BearerToken(ctx);
    var (token, duplicate, unauthorized) = store.Join(req);
    return unauthorized
        ? Results.Unauthorized()
        : Results.Ok(new PigeonJoinResult { Token = token, Duplicate = duplicate });
});

app.MapPost("/api/heartbeat", (HttpContext ctx) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    store.Heartbeat(id);
    return Results.NoContent();
});

app.MapGet("/api/who", (HttpContext ctx) =>
{
    if (AuthId(ctx) is null) return Unauthorized();
    return Results.Ok(store.Who());
});

app.MapPost("/api/status", (HttpContext ctx, PigeonStatusSet req) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    store.SetStatus(id, req.Status ?? "");
    return Results.Ok(new { });
});

// ── Channels ────────────────────────────────────────────────────────────────────────

app.MapGet("/api/channels", (HttpContext ctx) =>
{
    if (AuthId(ctx) is null) return Unauthorized();
    return Results.Ok(store.Channels());
});

app.MapPost("/api/channels", (HttpContext ctx, PigeonChannelCreate req) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    if (id != Store.OwnerId) return Results.Forbid(); // Phase 0: the owner creates rooms
    return Results.Ok(store.CreateChannel(req.Name, req.Description ?? ""));
});

app.MapPost("/api/channels/{channelId}/archive", (HttpContext ctx, string channelId) =>
{
    var id = AuthId(ctx);
    if (id is null) return Unauthorized();
    if (id != Store.OwnerId) return Results.Forbid();
    store.ArchiveChannel(channelId);
    return Results.NoContent();
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
    if (id != Store.OwnerId) return Results.Forbid(); // Phase 0: the owner seats the flock
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
        return Results.Forbid();
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

Console.WriteLine($"pigeon: listening on http://0.0.0.0:{config.Port} (data: {dataDir})");
Console.WriteLine($"pigeon: owner token: {config.OwnerToken}");
app.Run();

// ── Request payloads (web-shape) ────────────────────────────────────────────────────

public sealed record PigeonStatusSet(string? Status);
public sealed record PigeonChannelCreate(string Name, string? Description);
public sealed record PigeonMembership(string Identity, string Channel);

/// <summary>
/// Server config: data/pigeon.config.json. Created on first run with a generated
/// owner token (printed to the console).
/// </summary>
public sealed class ServerConfig
{
    public int Port { get; set; } = 8199;
    public string OwnerToken { get; set; } = "";
    /// <summary>Consecutive agent messages in a thread before it is tagged hot. 0 = off (default).</summary>
    public int HotThreshold { get; set; } = 0;

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
