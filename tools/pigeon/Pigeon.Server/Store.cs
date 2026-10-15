using System.Text;
using System.Text.Json;
using Pigeon.Client;

namespace Pigeon.Server;

/// <summary>
/// Pigeon data layer (docs/MESSANGER.md §6). Files, harness style:
///   data/identities.json          — identity cards + tokens + cursors + last_seen
///   data/channels/&lt;id&gt;.jsonl      — append-only message log (id = line number)
///   data/channels/&lt;id&gt;.meta.json  — name, description, created, archived, hot threads
/// The server is the SINGLE writer — instances never touch shared state directly.
/// All operations run under one lock (writes are small: a jsonl line, a meta file,
/// or an identities.json rewrite).
/// </summary>
public sealed class Store
{
    public const string OwnerId = "owner";
    public const int PresenceWindowSeconds = 45;

    private sealed class IdentityRec
    {
        public string Alias { get; set; } = "";
        public string Kind { get; set; } = "agent";
        public string Harness { get; set; } = "";
        public string Model { get; set; } = "";
        public string Status { get; set; } = "";
        public string Wake { get; set; } = "mention";
        public string[] Channels { get; set; } = [];
        public string Token { get; set; } = "";
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        /// <summary>Per-channel cursor: "read up to id N".</summary>
        public Dictionary<string, long> Cursors { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class ChannelRec
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTimeOffset Created { get; set; }
        public bool Archived { get; set; }
        /// <summary>Thread roots (0 = channel root) with ≥ hotThreshold consecutive agent messages.</summary>
        public long[] HotThreads { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions JsonCompact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();
    private readonly string _dataDir;
    private readonly string _channelsDir;
    private readonly int _hotThreshold;
    private readonly Dictionary<string, IdentityRec> _identities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChannelRec> _channels = new(StringComparer.Ordinal);
    /// <summary>Long-poll signals: one TCS per channel, swapped on every append.</summary>
    private readonly Dictionary<string, TaskCompletionSource> _signals = new(StringComparer.Ordinal);

    public Store(string dataDir, int hotThreshold = 0)
    {
        _dataDir = dataDir;
        _channelsDir = Path.Combine(dataDir, "channels");
        _hotThreshold = hotThreshold;
        Directory.CreateDirectory(_channelsDir);
        Load();
    }

    // ── Load / save ─────────────────────────────────────────────────────────────────

    private void Load()
    {
        lock (_gate)
        {
            var identitiesPath = Path.Combine(_dataDir, "identities.json");
            if (File.Exists(identitiesPath))
            {
                var recs = JsonSerializer.Deserialize<Dictionary<string, IdentityRec>>(
                    File.ReadAllText(identitiesPath), JsonIndented);
                if (recs is not null)
                    foreach (var (id, rec) in recs)
                        _identities[id] = rec;
            }

            // The owner (meat bag) is a participant too — a fixed identity, authenticated
            // with the config token, not a join token.
            if (!_identities.ContainsKey(OwnerId))
                _identities[OwnerId] = new IdentityRec
                {
                    Alias = "owner",
                    Kind = "human",
                    Harness = "web",
                    Wake = "none",
                    FirstSeen = DateTimeOffset.UtcNow,
                    LastSeen = DateTimeOffset.MinValue,
                };

            foreach (var metaPath in Directory.EnumerateFiles(_channelsDir, "*.meta.json"))
            {
                var id = Path.GetFileNameWithoutExtension(metaPath)[..^".meta".Length];
                var meta = JsonSerializer.Deserialize<ChannelRec>(File.ReadAllText(metaPath), JsonIndented);
                if (meta is null)
                    continue;
                _channels[id] = meta;
                // id = line number in the append-only log
                var logPath = Path.Combine(_channelsDir, id + ".jsonl");
                if (File.Exists(logPath))
                {
                    long lines = 0;
                    foreach (var _ in File.ReadLines(logPath))
                        lines++;
                    _ = lines; // NextId is derived lazily (line count) — nothing to store
                }
            }
        }
    }

    private void SaveIdentities()
    {
        var path = Path.Combine(_dataDir, "identities.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_identities, JsonIndented));
        File.Move(tmp, path, overwrite: true);
    }

    private void SaveChannelMeta(string channelId)
    {
        if (!_channels.TryGetValue(channelId, out var rec))
            return;
        var path = Path.Combine(_channelsDir, channelId + ".meta.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(rec, JsonIndented));
        File.Move(tmp, path, overwrite: true);
    }

    // ── Long-poll signals ───────────────────────────────────────────────────────────

    private TaskCompletionSource SignalFor(string channelId)
    {
        if (!_signals.TryGetValue(channelId, out var tcs))
        {
            tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _signals[channelId] = tcs;
        }
        return tcs;
    }

    private void Notify(string channelId)
    {
        var old = SignalFor(channelId);
        _signals[channelId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }

    // ── Identity ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Join / re-join. Rules (docs/MESSANGER.md §4): a matching token re-authenticates;
    /// a wrong token is rejected; no token → (re)issue one. A second LIVE client with
    /// the same id is flagged (duplicate), not rejected.
    /// </summary>
    public (string token, bool duplicate, bool unauthorized) Join(PigeonJoinRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Id))
            throw new ArgumentException("id is required");
        lock (_gate)
        {
            if (_identities.TryGetValue(req.Id, out var rec))
            {
                if (req.Token is not null && req.Token != rec.Token)
                    return ("", false, true);
                var duplicate = (DateTimeOffset.UtcNow - rec.LastSeen).TotalSeconds < PresenceWindowSeconds;
                if (req.Token is null)
                {
                    rec.Token = NewToken();
                    if (string.IsNullOrWhiteSpace(req.Alias))
                        req.Alias = rec.Alias;
                }
                ApplyCard(rec, req);
                rec.LastSeen = DateTimeOffset.UtcNow;
                SaveIdentities();
                return (rec.Token, duplicate, false);
            }

            var rec2 = new IdentityRec
            {
                Token = NewToken(),
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
            };
            ApplyCard(rec2, req);
            rec2.Alias = UniqueAlias(req.Id, req.Alias);
            _identities[req.Id] = rec2;
            SaveIdentities();
            return (rec2.Token, false, false);
        }
    }

    private static void ApplyCard(IdentityRec rec, PigeonJoinRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Alias))
            rec.Alias = req.Alias;
        if (!string.IsNullOrWhiteSpace(req.Kind))
            rec.Kind = req.Kind;
        if (!string.IsNullOrWhiteSpace(req.Harness))
            rec.Harness = req.Harness;
        if (!string.IsNullOrWhiteSpace(req.Model))
            rec.Model = req.Model;
        if (req.Status is not null)
            rec.Status = req.Status;
        if (!string.IsNullOrWhiteSpace(req.Wake))
            rec.Wake = req.Wake;
    }

    /// <summary>Unique alias: if taken by another id, append a short id suffix.</summary>
    private string UniqueAlias(string selfId, string requested)
    {
        var alias = string.IsNullOrWhiteSpace(requested) ? selfId[..Math.Min(8, selfId.Length)] : requested;
        var taken = _identities.Any(p => p.Key != selfId && string.Equals(p.Value.Alias, alias, StringComparison.Ordinal));
        while (taken)
        {
            alias = $"{alias}-{selfId[^4..].ToLowerInvariant()}";
            taken = _identities.Any(p => p.Key != selfId && string.Equals(p.Value.Alias, alias, StringComparison.Ordinal));
        }
        return alias;
    }

    private static string NewToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    /// <summary>Resolve a bearer token to an identity id (the owner token is handled by the caller).</summary>
    public string? AuthByToken(string token)
    {
        lock (_gate)
            return _identities.FirstOrDefault(p => p.Value.Token == token).Key;
    }

    public void Heartbeat(string id)
    {
        lock (_gate)
        {
            if (_identities.TryGetValue(id, out var rec))
            {
                rec.LastSeen = DateTimeOffset.UtcNow;
                SaveIdentities();
            }
        }
    }

    public void SetStatus(string id, string status)
    {
        lock (_gate)
        {
            if (_identities.TryGetValue(id, out var rec))
            {
                rec.Status = status;
                SaveIdentities();
            }
        }
    }

    public PigeonIdentity[] Who()
    {
        lock (_gate)
            return _identities
                .OrderBy(p => p.Value.Alias, StringComparer.OrdinalIgnoreCase)
                .Select(p => ToIdentity(p.Key, p.Value))
                .ToArray();
    }

    private static PigeonIdentity ToIdentity(string id, IdentityRec r) => new()
    {
        Id = id,
        Alias = r.Alias,
        Kind = r.Kind,
        Harness = r.Harness,
        Model = r.Model,
        Status = r.Status,
        Wake = r.Wake,
        Channels = r.Channels,
        FirstSeen = r.FirstSeen,
        LastSeen = r.LastSeen,
    };

    // ── Channels ────────────────────────────────────────────────────────────────────

    public PigeonChannel[] Channels()
    {
        lock (_gate)
            return _channels
                .OrderBy(p => p.Value.Created)
                .Select(p => ToChannel(p.Key, p.Value))
                .ToArray();
    }

    private PigeonChannel ToChannel(string id, ChannelRec r)
    {
        var hot = r.HotThreads.Contains(0);
        return new PigeonChannel
        {
            Id = id,
            Name = r.Name,
            Description = r.Description,
            Created = r.Created,
            Archived = r.Archived,
            Hot = hot,
        };
    }

    public PigeonChannel CreateChannel(string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required");
        lock (_gate)
        {
            var id = Slug(name);
            var n = 2;
            while (_channels.ContainsKey(id))
                id = $"{Slug(name)}-{n++}";
            var rec = new ChannelRec
            {
                Name = name.Trim(),
                Description = description?.Trim() ?? "",
                Created = DateTimeOffset.UtcNow,
            };
            _channels[id] = rec;
            SaveChannelMeta(id);
            return ToChannel(id, rec);
        }
    }

    public void ArchiveChannel(string channelId)
    {
        lock (_gate)
        {
            if (_channels.TryGetValue(channelId, out var rec))
            {
                rec.Archived = true;
                SaveChannelMeta(channelId);
            }
        }
    }

    /// <summary>Reassign a participant to a channel (leaves the old one — Phase 0 limit).</summary>
    public void Reassign(string identityId, string channelId)
    {
        lock (_gate)
        {
            if (!_channels.ContainsKey(channelId))
                throw new KeyNotFoundException(channelId);
            if (!_identities.TryGetValue(identityId, out var rec))
                throw new KeyNotFoundException(identityId);
            rec.Channels = [channelId];
            if (!rec.Cursors.ContainsKey(channelId))
                rec.Cursors[channelId] = 0;
            SaveIdentities();
        }
    }

    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var s = new string(chars).Trim('-');
        return s.Length == 0 ? "channel" : s[..Math.Min(32, s.Length)];
    }

    // ── Messages ────────────────────────────────────────────────────────────────────

    private long LastId(string channelId)
    {
        var logPath = Path.Combine(_channelsDir, channelId + ".jsonl");
        if (!File.Exists(logPath))
            return 0;
        long n = 0;
        foreach (var _ in File.ReadLines(logPath))
            n++;
        return n;
    }

    public PigeonMessage Send(PigeonSendRequest req, string authorId)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            throw new ArgumentException("text is required");
        lock (_gate)
        {
            if (!_channels.TryGetValue(req.Channel, out var ch))
                throw new KeyNotFoundException(req.Channel);
            if (ch.Archived)
                throw new InvalidOperationException("channel is archived");
            if (!_identities.TryGetValue(authorId, out var author))
                throw new KeyNotFoundException(authorId);
            if (!author.Channels.Contains(req.Channel, StringComparer.Ordinal))
                throw new UnauthorizedAccessException("not a member of the channel");

            long id = LastId(req.Channel) + 1;
            var msg = new PigeonMessage
            {
                Id = id,
                Channel = req.Channel,
                Thread = req.Thread,
                ReplyTo = req.ReplyTo,
                Author = new PigeonAuthor
                {
                    Id = authorId,
                    Alias = author.Alias,
                    Kind = author.Kind,
                    Harness = author.Harness,
                    Model = author.Model,
                },
                Mentions = req.Mentions.Select(ResolveMention).Where(m => m is not null).Select(m => m!).ToArray(),
                Ts = DateTimeOffset.UtcNow,
                Text = req.Text,
                Files = req.Files,
            };

            File.AppendAllText(
                Path.Combine(_channelsDir, req.Channel + ".jsonl"),
                JsonSerializer.Serialize(msg, JsonCompact) + "\n",
                new UTF8Encoding(false));

            UpdateHot(req.Channel, msg);
            SaveChannelMeta(req.Channel);
            Notify(req.Channel);
            return msg;
        }
    }

    /// <summary>Mentions arrive as ids or @aliases; resolve to ids, drop unknowns.</summary>
    private string? ResolveMention(string m)
    {
        var key = m.StartsWith('@') ? m[1..] : m;
        var byId = _identities.ContainsKey(key);
        if (byId)
            return key;
        return _identities.FirstOrDefault(p =>
                   string.Equals(p.Value.Alias, key, StringComparison.Ordinal)).Key;
    }

    /// <summary>
    /// Safety net (off by default): ≥ hotThreshold consecutive agent messages in a
    /// thread (channel root included, key 0) → the thread is tagged hot. A human
    /// message resets it.
    /// </summary>
    private void UpdateHot(string channelId, PigeonMessage msg)
    {
        if (_hotThreshold <= 0)
            return;
        if (!_channels.TryGetValue(channelId, out var ch))
            return;
        var threadKey = msg.Thread ?? 0;
        var logPath = Path.Combine(_channelsDir, channelId + ".jsonl");
        var lines = File.ReadLines(logPath).TakeLast(200).ToArray();

        if (msg.Author.Kind == "human")
        {
            var before = ch.HotThreads;
            ch.HotThreads = before.Where(t => t != threadKey).ToArray();
            return;
        }

        long consecutive = 0;
        foreach (var line in lines.Reverse())
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var m = JsonSerializer.Deserialize<PigeonMessage>(line, JsonCompact);
            if (m is null)
                break;
            if (m.Thread != msg.Thread)
                break;
            if (m.Author.Kind == "human")
                break;
            consecutive++;
            if (consecutive >= _hotThreshold)
            {
                if (!ch.HotThreads.Contains(threadKey))
                    ch.HotThreads = [.. ch.HotThreads, threadKey];
                break;
            }
        }
    }

    /// <summary>
    /// Long-poll read (docs/MESSANGER.md §6): messages after <paramref name="since"/>,
    /// advancing the reader's cursor; if none, waits for the channel signal up to
    /// <paramref name="waitMs"/> and reads once more.
    /// </summary>
    public async Task<PigeonMessage[]> Read(string channelId, long since, int waitMs, string readerId)
    {
        var first = ReadOnce(channelId, since, readerId);
        if (first.Length > 0 || waitMs <= 0)
            return first;

        TaskCompletionSource tcs;
        lock (_gate)
            tcs = SignalFor(channelId);
        try
        {
            using var cts = new CancellationTokenSource(waitMs);
            await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        return ReadOnce(channelId, since, readerId);
    }

    private PigeonMessage[] ReadOnce(string channelId, long since, string readerId)
    {
        lock (_gate)
        {
            if (!_channels.ContainsKey(channelId))
                throw new KeyNotFoundException(channelId);
            var logPath = Path.Combine(_channelsDir, channelId + ".jsonl");
            var result = new List<PigeonMessage>();
            if (File.Exists(logPath))
            {
                foreach (var line in File.ReadLines(logPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    var m = JsonSerializer.Deserialize<PigeonMessage>(line, JsonCompact);
                    if (m is null || m.Id <= since)
                        continue;
                    result.Add(m);
                }
            }
            if (result.Count > 0 && _identities.TryGetValue(readerId, out var rec))
            {
                rec.Cursors[channelId] = result.Max(m => m.Id);
                SaveIdentities();
            }
            return [.. result];
        }
    }

    /// <summary>The last N messages of a channel, newest last. Does not touch cursors.</summary>
    public PigeonMessage[] Peek(string channelId, int count)
    {
        lock (_gate)
        {
            if (!_channels.ContainsKey(channelId))
                throw new KeyNotFoundException(channelId);
            var logPath = Path.Combine(_channelsDir, channelId + ".jsonl");
            if (!File.Exists(logPath))
                return [];
            return File.ReadLines(logPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<PigeonMessage>(l, JsonCompact))
                .Where(m => m is not null)
                .Select(m => m!)
                .TakeLast(Math.Max(1, count))
                .ToArray();
        }
    }

    public PigeonUnread[] Unread(string identityId)
    {
        lock (_gate)
        {
            if (!_identities.TryGetValue(identityId, out var rec))
                throw new KeyNotFoundException(identityId);
            return rec.Channels
                .Where(_channels.ContainsKey)
                .Select(c => new PigeonUnread
                {
                    Channel = c,
                    Since = rec.Cursors.GetValueOrDefault(c, 0),
                    Count = Math.Max(0, (int)(LastId(c) - rec.Cursors.GetValueOrDefault(c, 0))),
                })
                .ToArray();
        }
    }
}
