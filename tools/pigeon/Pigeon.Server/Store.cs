using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// A host token that is stable enough to tell one box from another: loopback is spelled
    /// out so it reads as "this machine", and IPv6 scopes are dropped. Deliberately not a
    /// reverse-DNS lookup — on this network 127.0.0.1 resolves to "kubernetes.docker.internal",
    /// which would put a lie on every participant card.
    /// </summary>
    public static string HostOf(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null)
            return "";
        if (System.Net.IPAddress.IsLoopback(ip))
            return "localhost";
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return ip.ToString().Split('%')[0];
        return ip.ToString();
    }

    private sealed class IdentityRec
    {
        public string Alias { get; set; } = "";
        public string Kind { get; set; } = "agent";
        public string Harness { get; set; } = "";
        public string Model { get; set; } = "";
        public string Status { get; set; } = "";
        /// <summary>Self-reported coarse state (idle | generating | dispatching | hung | errored) and a line of context.</summary>
        public string State { get; set; } = "";
        public string Activity { get; set; } = "";
        public DateTimeOffset StateAt { get; set; }
        public string Wake { get; set; } = "mention";
        public string[] Channels { get; set; } = [];
        public string Token { get; set; } = "";
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        /// <summary>Per-channel cursor: "read up to id N".</summary>
        public Dictionary<string, long> Cursors { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Last connection label reported by the client ("mcp#abcd@10.0.0.7"); not part of identity.</summary>
        public string Connection { get; set; } = "";
        /// <summary>Groups joined voluntarily.</summary>
        public string[] Roles { get; set; } = [];
        /// <summary>Groups the owner put this participant in; not leavable by them.</summary>
        public string[] Assigned { get; set; } = [];
    }

    private sealed class GroupRec
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTimeOffset Created { get; set; }
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
    private readonly string _filesDir;
    private readonly int _hotThreshold;
    private DateTimeOffset _lastIdentitySave = DateTimeOffset.MinValue;
    private readonly Dictionary<string, IdentityRec> _identities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChannelRec> _channels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupRec> _groups = new(StringComparer.Ordinal);
    /// <summary>Long-poll signals: one TCS per channel, swapped on every append.</summary>
    private readonly Dictionary<string, TaskCompletionSource> _signals = new(StringComparer.Ordinal);

    public Store(string dataDir, int hotThreshold = 0)
    {
        _dataDir = dataDir;
        _channelsDir = Path.Combine(dataDir, "channels");
        _filesDir = Path.Combine(dataDir, "files");
        _hotThreshold = hotThreshold;
        Directory.CreateDirectory(_channelsDir);
        Directory.CreateDirectory(_filesDir);
        Load();
        LoadFileMeta();
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

            var groupsPath = Path.Combine(_dataDir, "groups.json");
            if (File.Exists(groupsPath))
            {
                var groups = JsonSerializer.Deserialize<Dictionary<string, GroupRec>>(
                    File.ReadAllText(groupsPath), JsonIndented);
                if (groups is not null)
                    foreach (var (id, rec) in groups)
                        _groups[id] = rec;
            }
        }
    }

    private void SaveGroups()
    {
        var path = Path.Combine(_dataDir, "groups.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_groups, JsonIndented));
        File.Move(tmp, path, overwrite: true);
    }

    private void SaveIdentities()
    {
        var path = Path.Combine(_dataDir, "identities.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_identities, JsonIndented));
        File.Move(tmp, path, overwrite: true);
        _lastIdentitySave = DateTimeOffset.UtcNow;
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
    /// a missing or wrong token on an EXISTING id is rejected (never rotates — that path
    /// used to let anyone hijack a live identity). A NEW id with no token mints one.
    /// A second LIVE client with the same id is flagged (duplicate), not rejected.
    /// </summary>
    /// <summary>
    /// Join performed with the owner's master token: create the identity if it is absent,
    /// otherwise update its card. The existing token is returned untouched — seating someone
    /// as an admin must never rotate a credential that client is already using.
    /// </summary>
    public (string token, bool duplicate, bool created) AdminJoin(PigeonJoinRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Id))
            throw new ArgumentException("id is required");
        lock (_gate)
        {
            if (_identities.TryGetValue(req.Id, out var rec))
            {
                var duplicate = (DateTimeOffset.UtcNow - rec.LastSeen).TotalSeconds < PresenceWindowSeconds;
                ApplyCard(req.Id, rec, req);
                rec.LastSeen = DateTimeOffset.UtcNow;
                SaveIdentities();
                return (rec.Token, duplicate, false);
            }
            var fresh = new IdentityRec
            {
                Token = NewToken(),
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
            };
            ApplyCard(req.Id, fresh, req);
            fresh.Alias = UniqueAlias(req.Id, req.Alias);
            _identities[req.Id] = fresh;
            SaveIdentities();
            return (fresh.Token, false, true);
        }
    }

    public (string token, bool duplicate, bool unauthorized) Join(PigeonJoinRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Id))
            throw new ArgumentException("id is required");
        lock (_gate)
        {
            if (_identities.TryGetValue(req.Id, out var rec))
            {
                // Re-authenticating an EXISTING identity requires the held token. A missing
                // or wrong token is rejected — never a rotation. Anonymous join to an
                // occupied id used to rotate the token and hand the fresh one to the caller:
                // the victim's live session was hijacked (old bearer → 401, attacker in).
                // Token recovery is the owner's move (forget the identity, client re-joins).
                if (req.Token is null || req.Token != rec.Token)
                    return ("", false, true);
                var duplicate = (DateTimeOffset.UtcNow - rec.LastSeen).TotalSeconds < PresenceWindowSeconds;
                ApplyCard(req.Id, rec, req);
                rec.LastSeen = DateTimeOffset.UtcNow;
                SaveIdentities();
                return (rec.Token, duplicate, false);
            }

            var rec2 = new IdentityRec
            {
                // A caller-supplied token on a NEW identity is a key, not a rotation: when the
                // key is the identity, adopting the key the caller already holds is the whole
                // point. Without one, mint a random token as before.
                Token = string.IsNullOrWhiteSpace(req.Token) ? NewToken() : req.Token,
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
            };
            ApplyCard(req.Id, rec2, req);
            rec2.Alias = UniqueAlias(req.Id, req.Alias);
            _identities[req.Id] = rec2;
            SaveIdentities();
            return (rec2.Token, false, false);
        }
    }

    private void ApplyCard(string id, IdentityRec rec, PigeonJoinRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Alias))
            rec.Alias = UniqueAlias(id, req.Alias);
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

    /// <summary>
    /// Is this label already owned by a keyed identity ("mcp:opencode-&lt;tail&gt;")? A
    /// keyless client must not inherit the bare "mcp:opencode" after that: the key is the
    /// name, and a client without one has no claim to it.
    /// </summary>
    public bool LabelOwnedByKey(string label)
    {
        var prefix = "mcp:" + label + "-";
        lock (_gate)
        {
            return _identities.Any(p =>
                p.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(p.Value.Token));
        }
    }

    /// <summary>Unique alias: if taken by another id, append a short id suffix.</summary>
    private string UniqueAlias(string selfId, string requested)
    {
        var alias = string.IsNullOrWhiteSpace(requested) ? selfId[..Math.Min(8, selfId.Length)] : requested;
        if (!IsTaken(alias, selfId))
            return alias;

        // Disambiguate on the id tail, which is informative ("mcp:x@10.0.0.7" -> "-0-7"). It has
        // to be mentionable: @tokens match [A-Za-z0-9][A-Za-z0-9_-]* and stop at a dot, so an
        // unsanitized tail would make the participant impossible to address by name.
        var tail = MentionSafe(selfId.Length <= 4 ? selfId : selfId[^4..]);
        if (tail.Length > 0 && !IsTaken($"{alias}-{tail}", selfId))
            return $"{alias}-{tail}";

        for (var n = 2; ; n++)
        {
            var candidate = $"{alias}-{n}";
            if (!IsTaken(candidate, selfId))
                return candidate;
        }
    }

    private bool IsTaken(string alias, string selfId) =>
        _identities.Any(p => p.Key != selfId && string.Equals(p.Value.Alias, alias, StringComparison.Ordinal));

    /// <summary>Trim anything outside the @mention alphabet, so the result can be typed as @name.</summary>
    private static string MentionSafe(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return sb.ToString().Trim('-');
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

    /// <summary>
    /// Heartbeat with an optional state. A participant that only says "I am alive" tells the
    /// room less than one that says "alive, and currently generating". Empty state leaves the
    /// previous one untouched; a state is never inferred here — the server stores claims, it
    /// does not judge them.
    /// </summary>
    public void Heartbeat(string id, string? state, string? activity)
    {
        lock (_gate)
        {
            if (!_identities.TryGetValue(id, out var rec))
                return;
            var changedState = !string.IsNullOrWhiteSpace(state) && rec.State != state.Trim().ToLowerInvariant();
            rec.LastSeen = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(state))
            {
                rec.State = state.Trim().ToLowerInvariant();
                rec.Activity = activity?.Trim() ?? "";
                rec.StateAt = DateTimeOffset.UtcNow;
            }
            // Presence ticks every few seconds; writing the whole identity file every tick is
            // churn for a value that is only read as "recent or not". Persist on change, and
            // otherwise at most every 15 s.
            if (changedState || (DateTimeOffset.UtcNow - _lastIdentitySave).TotalSeconds >= 15)
                SaveIdentities();
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

    /// <summary>Note which connection a participant is currently reachable through (no-op if unchanged).</summary>
    public void SetConnection(string identityId, string connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
            return;
        lock (_gate)
        {
            if (!_identities.TryGetValue(identityId, out var rec) || rec.Connection == connection)
                return;
            rec.Connection = connection;
            SaveIdentities();
        }
    }

    /// <summary>
    /// May this identity speak in this channel? The owner is everywhere by definition: they
    /// run the server and seat everyone else, so requiring them to hold a seat is a bug, not
    /// a permission. Callers already hold the lock.
    /// </summary>
    private bool IsMemberLocked(string identityId, string channelId) =>
        identityId == OwnerId || _identities[identityId].Channels.Contains(channelId, StringComparer.Ordinal);

    /// <summary>Public form of the membership check (takes the lock itself).</summary>
    public bool IsMember(string identityId, string channelId)
    {
        lock (_gate)
            return IsMemberLocked(identityId, channelId);
    }

    /// <summary>The connection label of an identity, or "" if unknown (does not throw).</summary>
    public string ConnectionOf(string identityId)
    {
        lock (_gate)
            return _identities.TryGetValue(identityId, out var rec) ? rec.Connection : "";
    }

    /// <summary>
    /// True when an identity with this exact id already exists — the check that lets the MCP
    /// adapter tell "the same box coming back" from "a second box with the same client name"
    /// (<see cref="Join"/> has no equivalent: it treats a known id as re-auth and a second live
    /// client on it as a silent duplicate, alias included).
    /// </summary>
    public bool Exists(string identityId)
    {
        lock (_gate)
            return _identities.ContainsKey(identityId);
    }

    /// <summary>
    /// Drop a participant card: token, membership, cursors, roles. Messages stay — they are
    /// history and carry the author inline. Cards otherwise live forever, so a server that
    /// has seen a dozen throwaway clients ends up with a roster that is mostly ghosts.
    /// </summary>
    public bool Forget(string identityId)
    {
        lock (_gate)
        {
            // Membership lives on the identity card, not on the channel, so dropping the card
            // is enough. Messages stay — they are history and carry the author inline.
            if (identityId == OwnerId || !_identities.Remove(identityId))
                return false;
            SaveIdentities();
            return true;
        }
    }

    // ── Groups ──────────────────────────────────────────────────────────────────────
    // An address that reaches a KIND of agent instead of a specific one. Nothing is built in:
    // if the owner wants everyone to answer to one name, they create a group and put people
    // in it. Members join of their own accord where they can; the owner's word is binding,
    // because an owner-appointed critic who could walk out is not a critic.

    public PigeonGroup[] Groups()
    {
        lock (_gate)
            return _groups
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => new PigeonGroup
                {
                    Id = p.Key,
                    Name = p.Value.Name,
                    Description = p.Value.Description,
                    Created = p.Value.Created,
                    Members = MembersOfLocked(p.Key),
                })
                .ToArray();
    }

    /// <summary>Create a group. False when the name is taken or not addressable.</summary>
    public bool CreateGroup(string name, string description)
    {
        var id = GroupSlug(name);
        lock (_gate)
        {
            if (string.IsNullOrEmpty(id) || _groups.ContainsKey(id))
                return false;
            _groups[id] = new GroupRec
            {
                Name = string.IsNullOrWhiteSpace(name) ? id : name,
                Description = description,
                Created = DateTimeOffset.UtcNow,
            };
            SaveGroups();
            return true;
        }
    }

    public bool DeleteGroup(string groupId)
    {
        lock (_gate)
        {
            if (!_groups.Remove(groupId))
                return false;
            foreach (var rec in _identities.Values)
            {
                rec.Roles = rec.Roles.Where(g => g != groupId).ToArray();
                rec.Assigned = rec.Assigned.Where(g => g != groupId).ToArray();
            }
            SaveGroups();
            SaveIdentities();
            return true;
        }
    }

    /// <summary>The owner puts a participant in a group. They cannot leave it afterwards.</summary>
    public void AssignRole(string identityId, string groupId)
    {
        lock (_gate)
        {
            if (!_identities.TryGetValue(identityId, out var rec) || !_groups.ContainsKey(groupId))
                return;
            if (!rec.Assigned.Contains(groupId, StringComparer.Ordinal))
            {
                rec.Assigned = [.. rec.Assigned, groupId];
                SaveIdentities();
            }
        }
    }

    public void UnassignRole(string identityId, string groupId)
    {
        lock (_gate)
        {
            if (!_identities.TryGetValue(identityId, out var rec))
                return;
            var after = rec.Assigned.Where(g => g != groupId).ToArray();
            if (after.Length != rec.Assigned.Length)
            {
                rec.Assigned = after;
                SaveIdentities();
            }
        }
    }

    /// <summary>Voluntary join. Refused for a group the owner assigned — that one is binding.</summary>
    public string? JoinGroup(string identityId, string groupId, bool voluntary)
    {
        lock (_gate)
        {
            if (!_identities.TryGetValue(identityId, out var rec) || !_groups.ContainsKey(groupId))
                return $"No such group '{groupId}'.";
            if (rec.Assigned.Contains(groupId, StringComparer.Ordinal))
                return $"The owner put you in @{groupId}; you cannot leave it.";
            if (rec.Roles.Contains(groupId, StringComparer.Ordinal))
                return $"Already in @{groupId}.";
            rec.Roles = [.. rec.Roles, groupId];
            SaveIdentities();
            return null;
        }
    }

    public string? LeaveGroup(string identityId, string groupId)
    {
        lock (_gate)
        {
            if (!_identities.TryGetValue(identityId, out var rec))
                return $"No such participant '{identityId}'.";
            if (rec.Assigned.Contains(groupId, StringComparer.Ordinal))
                return $"The owner put you in @{groupId}; only they can remove you.";
            if (!rec.Roles.Contains(groupId, StringComparer.Ordinal))
                return $"You are not in @{groupId}.";
            rec.Roles = rec.Roles.Where(g => g != groupId).ToArray();
            SaveIdentities();
            return null;
        }
    }

    /// <summary>Groups an identity belongs to: what it chose, plus what it was given.</summary>
    public string[] RolesOf(string identityId)
    {
        lock (_gate)
            return _identities.TryGetValue(identityId, out var rec)
                ? [.. rec.Roles.Concat(rec.Assigned).Distinct(StringComparer.Ordinal)]
                : [];
    }

    /// <summary>Ids in a group, owner-assigned and voluntary alike.</summary>
    public string[] MembersOf(string groupId)
    {
        lock (_gate)
            return _groups.ContainsKey(groupId) ? MembersOfLocked(groupId) : [];
    }

    /// <summary>Caller holds the lock.</summary>
    private string[] MembersOfLocked(string groupId)
    {
        return _identities
            .Where(p => p.Value.Assigned.Contains(groupId, StringComparer.Ordinal)
                     || p.Value.Roles.Contains(groupId, StringComparer.Ordinal))
            .OrderBy(p => p.Value.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Key)
            .ToArray();
    }

    /// <summary>Group names must be @token-addressable, same alphabet as aliases.</summary>
    private static string GroupSlug(string name) =>
        string.IsNullOrWhiteSpace(name) ? "" : Slug(name);

    public PigeonIdentity[] Who()
    {
        lock (_gate)
            return _identities
                .OrderBy(p => p.Value.Alias, StringComparer.OrdinalIgnoreCase)
                .Select(p => ToIdentity(p.Key, p.Value))
                .ToArray();
    }

    private PigeonIdentity ToIdentity(string id, IdentityRec r) => new()
    {
        Id = id,
        Alias = r.Alias,
        Kind = r.Kind,
        Harness = r.Harness,
        Model = r.Model,
        Status = r.Status,
        State = r.State,
        Activity = r.Activity,
        StateAt = r.StateAt,
        Wake = r.Wake,
        // The owner holds no seat; their channel list is every channel that exists.
        Channels = id == OwnerId ? _channels.Keys.ToArray() : r.Channels,
        Roles = r.Roles,
        Assigned = r.Assigned,
        FirstSeen = r.FirstSeen,
        LastSeen = r.LastSeen,
        Connection = r.Connection,
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

    /// <summary>
    /// Change what a channel is for. The description is not decoration: it lands in every
    /// member's state block, so editing it changes what agents think the room is about.
    /// </summary>
    public bool DescribeChannel(string channelId, string description)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(channelId, out var rec))
                return false;
            rec.Description = description?.Trim() ?? "";
            SaveChannelMeta(channelId);
            return true;
        }
    }

    /// <summary>
    /// Rename a channel (owner-only, checked by the caller). The id is NOT changed — it is
    /// the slug the message log and every membership reference point at; only the display
    /// name (and optionally the description) move.
    /// </summary>
    public bool RenameChannel(string channelId, string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required");
        lock (_gate)
        {
            if (!_channels.TryGetValue(channelId, out var rec))
                return false;
            rec.Name = name.Trim();
            if (description is not null)
                rec.Description = description.Trim();
            SaveChannelMeta(channelId);
            return true;
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

    /// <summary>
    /// Delete a channel (owner-only, checked by the caller): message log + meta files,
    /// membership cleared from all identity cards.
    /// </summary>
    public void DeleteChannel(string channelId)
    {
        lock (_gate)
        {
            if (!_channels.ContainsKey(channelId))
                throw new KeyNotFoundException(channelId);
            foreach (var rec in _identities.Values)
            {
                rec.Channels = rec.Channels
                    .Where(c => !string.Equals(c, channelId, StringComparison.Ordinal))
                    .ToArray();
            }
            _channels.Remove(channelId);
            foreach (var file in new[] { ".jsonl", ".meta.json" })
            {
                var path = Path.Combine(_channelsDir, channelId + file);
                if (File.Exists(path))
                    File.Delete(path);
            }
            SaveIdentities();
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
            if (!IsMemberLocked(authorId, req.Channel))
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
                // The author never lands in their own mentions. It used to, and it was an
                // exotic curiosity; with groups it is routine — "@critics" written by a critic
                // would otherwise wake the critic. A self-mention can never be actionable.
                Mentions = ParseMentions(req.Text).Where(id => id != authorId).ToArray(),
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

    /// <summary>
    /// @-токены в тексте сообщения (решение владельца, 2026-10-15): отправляющий пишет
    /// "@alias" или "@guid" прямо в тексте, сервер резолвит в id участника. Отдельного
    /// поля mentions в запросе нет — текст есть контракт.
    ///
    /// "@\" экранирует упоминание. Без этого невозможно НАПИСАТЬ об имени, не разбудив
    /// его: резолв чисто текстовый, и перечисление "@probe, @critic2, ..." в ответе
    /// разбудило шесть участников, которые в этом ответе не упоминались. Побег стоит перед
    /// "@", поэтому не попадает в алфавит токена и резолвится в ноль — привычка читается
    /// сразу и ничего не ломает.
    /// </summary>
    private static readonly Regex MentionPattern = new(@"(?<!\\)@([A-Za-z0-9][A-Za-z0-9_-]*)", RegexOptions.Compiled);

    /// <summary>Resolve all @tokens in the text to participant ids; unknowns are dropped.</summary>
    private string[] ParseMentions(string text)
    {
        var result = new List<string>();
        foreach (Match m in MentionPattern.Matches(text))
        {
            foreach (var id in ResolveMention(m.Groups[1].Value))
                if (!result.Contains(id, StringComparer.Ordinal))
                    result.Add(id);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Resolve one mention token to everything it reaches: an exact participant id, then an
    /// alias, then a group, then a UNIQUE id prefix (git-style: @5a7ae119 →
    /// 5a7ae119c1554c798b5fa34d50d6646c — guids are 32 hex chars, 8 is collision-safe).
    ///
    /// A token can match more than one of these and ALL of them are returned. Names here are
    /// idiosyncratic (guids, "opencode") so collisions with a group name are rare, and an
    /// address that silently picked a winner would be worse than one that reaches both — the
    /// owner can rename a group in a single call.
    /// </summary>
    private string[] ResolveMention(string key)
    {
        var result = new List<string>();
        void Add(string id)
        {
            if (!result.Contains(id, StringComparer.Ordinal))
                result.Add(id);
        }

        if (_identities.ContainsKey(key))
            Add(key);

        var byAlias = _identities.FirstOrDefault(p =>
            string.Equals(p.Value.Alias, key, StringComparison.Ordinal));
        if (byAlias.Key is not null)
            Add(byAlias.Key);

        if (_groups.ContainsKey(key))
            foreach (var member in MembersOfLocked(key))
                Add(member);

        if (key.Length >= 8)
        {
            foreach (var match in _identities
                .Where(p => p.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Key))
                Add(match);
        }
        return result.ToArray();
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

    /// <summary>
    /// Inclusive id range [from, to]; <paramref name="to"/> &lt;= 0 means "to the head".
    /// The reader cursor moves to the highest id actually returned, and NEVER backwards — a
    /// bounded or historical read must not mark unread messages as seen. <see cref="Read"/>
    /// cannot express either bound: it returns everything above <c>since</c> and advances to
    /// the head, which silently swallows everything the caller chose not to show.
    /// </summary>
    public PigeonMessage[] ReadRange(string channelId, long from, long to, string readerId)
    {
        lock (_gate)
        {
            if (!_channels.ContainsKey(channelId))
                throw new KeyNotFoundException(channelId);
            var logPath = Path.Combine(_channelsDir, channelId + ".jsonl");
            var result = new List<PigeonMessage>();
            var head = 0L;
            if (File.Exists(logPath))
            {
                foreach (var line in File.ReadLines(logPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    var m = JsonSerializer.Deserialize<PigeonMessage>(line, JsonCompact);
                    if (m is null)
                        continue;
                    head = Math.Max(head, m.Id);
                    if (m.Id > from && (to <= 0 || m.Id <= to))
                        result.Add(m);
                }
            }
            if (result.Count > 0 && _identities.TryGetValue(readerId, out var rec))
            {
                var reached = result.Max(m => m.Id);
                var had = rec.Cursors.GetValueOrDefault(channelId, 0);
                if (reached > had)
                {
                    rec.Cursors[channelId] = reached;
                    SaveIdentities();
                }
            }
            return [.. result];
        }
    }

    /// <summary>The highest message id in a channel (0 when empty).</summary>
    public long Head(string channelId)
    {
        lock (_gate)
        {
            if (!_channels.ContainsKey(channelId))
                throw new KeyNotFoundException(channelId);
            return LastId(channelId);
        }
    }

    /// <summary>
    /// Where every participant stands in one channel — the read receipts. Only identities
    /// seated in the channel (the owner included, who is seated everywhere) are listed.
    /// </summary>
    public PigeonCursor[] Cursors(string channelId)
    {
        lock (_gate)
        {
            if (!_channels.ContainsKey(channelId))
                throw new KeyNotFoundException(channelId);
            var head = LastId(channelId);
            return _identities
                .Where(p => p.Key == OwnerId || p.Value.Channels.Contains(channelId, StringComparer.Ordinal))
                .OrderBy(p => p.Value.Alias, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    var since = p.Value.Cursors.GetValueOrDefault(channelId, 0);
                    return new PigeonCursor
                    {
                        Identity = p.Key,
                        Alias = p.Value.Alias,
                        Channel = channelId,
                        Since = since,
                        Head = head,
                        Unread = Math.Max(0, (int)(head - since)),
                    };
                })
                .ToArray();
        }
    }

    public PigeonUnread[] Unread(string identityId)
    {
        lock (_gate)
        {
            if (!_identities.TryGetValue(identityId, out var rec))
                throw new KeyNotFoundException(identityId);
            // The owner is in every channel, so their unread view spans all of them.
            var channels = identityId == OwnerId
                ? _channels.Keys.ToArray()
                : rec.Channels.Where(_channels.ContainsKey).ToArray();
            return channels
                .Select(c => new PigeonUnread
                {
                    Channel = c,
                    Since = rec.Cursors.GetValueOrDefault(c, 0),
                    Count = Math.Max(0, (int)(LastId(c) - rec.Cursors.GetValueOrDefault(c, 0))),
                })
                .ToArray();
        }
    }

    // ── File pool (docs/MESSANGER.md §7, path-addressed mutable model) ──────────────

    private sealed class FileMeta
    {
        public string Owner { get; set; } = "";
        public long Size { get; set; }
        public DateTimeOffset Mtime { get; set; }
        public string Hash { get; set; } = "";
    }

    private readonly Dictionary<string, FileMeta> _fileMeta = new(StringComparer.Ordinal);
    private string _fileMetaPath => Path.Combine(_dataDir, "files.meta.json");

    private void LoadFileMeta()
    {
        if (File.Exists(_fileMetaPath))
        {
            try
            {
                _fileMeta.Clear();
                var recs = JsonSerializer.Deserialize<Dictionary<string, FileMeta>>(
                    File.ReadAllText(_fileMetaPath), JsonIndented);
                if (recs is not null)
                    foreach (var (k, v) in recs)
                        _fileMeta[k] = v;
            }
            catch { /* corrupt meta — start fresh */ }
        }
    }

    private void SaveFileMeta()
    {
        var tmp = _fileMetaPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_fileMeta, JsonIndented));
        File.Move(tmp, _fileMetaPath, overwrite: true);
    }

    /// <summary>
    /// Validate a pool-relative path: no "..", no absolute, no drive letter, no null bytes.
    /// Returns the normalized path (forward slashes, no leading "/").
    /// </summary>
    private static string ValidatePath(string path)
    {
        // "" = root (Path.IsPathRooted("") is true on Windows, so check before rooted).
        if (string.IsNullOrEmpty(path))
            return "";
        if (path.Contains('\0'))
            throw new ArgumentException("path contains null byte");
        if (Path.IsPathRooted(path))
            throw new ArgumentException($"path must be relative, got rooted: {path}");
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains(".."))
            throw new ArgumentException($"path must not contain '..': {path}");
        return normalized;
    }

    private static bool IsBinaryExt(string path) => path is
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico" or
        ".glb" or ".gltf" or ".obj" or ".fbx" or ".stl" or
        ".mp4" or ".webm" or ".avi" or ".mov" or
        ".zip" or ".tar" or ".gz" or ".7z" or ".rar" or
        ".pdf" or ".woff" or ".woff2" or ".ttf" or ".otf" or
        ".bin" or ".dat" or ".exe" or ".dll" or ".so";

    private string ResolveFile(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_filesDir, path));
        var root = Path.GetFullPath(_filesDir) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal) && full != Path.GetFullPath(_filesDir))
            throw new ArgumentException($"path escapes the pool: {path}");
        return full;
    }

    public PigeonFileEntry[] FileLs(string path)
    {
        lock (_gate)
        {
            var rel = ValidatePath(path);
            var full = ResolveFile(rel);
            if (!Directory.Exists(full) && !File.Exists(full))
                throw new KeyNotFoundException(path);
            var entries = new List<PigeonFileEntry>();
            if (Directory.Exists(full))
            {
                foreach (var dir in Directory.GetDirectories(full))
                    entries.Add(new PigeonFileEntry { Name = Path.GetFileName(dir), IsDir = true });
                foreach (var f in Directory.GetFiles(full))
                {
                    var name = Path.GetFileName(f);
                    // ValidatePath returns "" for the root (never "/"), so test for empty.
                    var relPath = rel == "" ? name : $"{rel.TrimEnd('/')}/{name}";
                    var meta = _fileMeta.GetValueOrDefault(relPath);
                    entries.Add(new PigeonFileEntry
                    {
                        Name = name,
                        IsDir = false,
                        Size = new FileInfo(f).Length,
                        Owner = meta?.Owner ?? "",
                        Mtime = meta?.Mtime ?? new FileInfo(f).LastWriteTimeUtc,
                        Hash = meta?.Hash ?? "",
                    });
                }
                entries.Sort((a, b) => a.IsDir != b.IsDir ? (a.IsDir ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                var relPath = rel.TrimStart('/');
                var meta = _fileMeta.GetValueOrDefault(relPath);
                entries.Add(new PigeonFileEntry
                {
                    Name = Path.GetFileName(rel),
                    IsDir = false,
                    Size = new FileInfo(full).Length,
                    Owner = meta?.Owner ?? "",
                    Mtime = meta?.Mtime ?? new FileInfo(full).LastWriteTimeUtc,
                    Hash = meta?.Hash ?? "",
                });
            }
            return entries.ToArray();
        }
    }

    /// <summary>Returns (bytes, isText). Caller decides how to serialize.</summary>
    public (byte[] Data, bool IsText) FileGet(string path)
    {
        lock (_gate)
        {
            var rel = ValidatePath(path).TrimStart('/');
            var full = ResolveFile(rel);
            if (!File.Exists(full))
                throw new KeyNotFoundException(path);
            var data = File.ReadAllBytes(full);
            return (data, !IsBinaryExt(Path.GetExtension(full)));
        }
    }

    public PigeonFileResult FilePut(string path, byte[] data, string owner)
    {
        lock (_gate)
        {
            var rel = ValidatePath(path).TrimStart('/');
            var full = ResolveFile(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
            var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            _fileMeta[rel] = new FileMeta
            {
                Owner = owner,
                Size = data.Length,
                Mtime = DateTimeOffset.UtcNow,
                Hash = hash,
            };
            SaveFileMeta();
            return new PigeonFileResult { Path = rel, Size = data.Length, Hash = hash };
        }
    }

    /// <summary>Server-side old→new edit (like edit_file). Returns false if old_text not found exactly once.</summary>
    public bool FileEdit(string path, string oldText, string newText)
    {
        lock (_gate)
        {
            var rel = ValidatePath(path).TrimStart('/');
            var full = ResolveFile(rel);
            if (!File.Exists(full))
                throw new KeyNotFoundException(path);
            var content = File.ReadAllText(full);
            var count = CountOccurrences(content, oldText);
            if (count != 1)
                return false;
            var updated = content.Replace(oldText, newText);
            File.WriteAllText(full, updated);
            var meta = _fileMeta.GetValueOrDefault(rel) ?? new FileMeta();
            meta.Size = System.Text.Encoding.UTF8.GetByteCount(updated);
            meta.Mtime = DateTimeOffset.UtcNow;
            meta.Hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(updated))).ToLowerInvariant();
            _fileMeta[rel] = meta;
            SaveFileMeta();
            return true;
        }
    }

    public void FileDelete(string path)
    {
        lock (_gate)
        {
            var rel = ValidatePath(path).TrimStart('/');
            var full = ResolveFile(rel);
            if (!File.Exists(full) && !Directory.Exists(full))
                throw new KeyNotFoundException(path);
            if (Directory.Exists(full))
                Directory.Delete(full, recursive: true);
            else
                File.Delete(full);
            _fileMeta.Remove(rel);
            // Remove any sub-paths (for directory delete).
            var prefix = rel + "/";
            var toRemove = _fileMeta.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            foreach (var k in toRemove)
                _fileMeta.Remove(k);
            SaveFileMeta();
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
