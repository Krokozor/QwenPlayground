namespace Pigeon.Client;

/// <summary>
/// Pigeon — messenger for Qwens (docs/MESSANGER.md). Envelope models shared by the
/// server, the harness glue, the web UI and (later) the MCP adapter.
/// </summary>

/// <summary>Author of a message — denormalized into the envelope so a reader never needs a second lookup.</summary>
public sealed class PigeonAuthor
{
    public string Id { get; set; } = "";
    public string Alias { get; set; } = "";
    /// <summary>human | agent | bridge.</summary>
    public string Kind { get; set; } = "agent";
    /// <summary>Which body it writes from: qwen | gemma | mcp:… | … (secondary, but visible).</summary>
    public string Harness { get; set; } = "";
    /// <summary>Self-reported at join (llama.cpp /props) — "who am I talking to".</summary>
    public string Model { get; set; } = "";
}

/// <summary>Message envelope (docs/MESSANGER.md §6).</summary>
public sealed class PigeonMessage
{
    /// <summary>Monotonic per channel (id = line number in the channel's jsonl).</summary>
    public long Id { get; set; }
    public string Channel { get; set; } = "";
    /// <summary>Id of the thread's root message; null = channel root. A task is a thread with a title.</summary>
    public long? Thread { get; set; }
    public long? ReplyTo { get; set; }
    public PigeonAuthor Author { get; set; } = new();
    /// <summary>
    /// Mentioned participant ids — resolved by the SERVER from @tokens in the text
    /// ("@alias" or "@guid"); the sender never sends them explicitly.
    /// </summary>
    public string[] Mentions { get; set; } = [];
    /// <summary>Server time.</summary>
    public DateTimeOffset Ts { get; set; }
    public string Text { get; set; } = "";
    /// <summary>sha256 refs into the shared file pool (Phase 1).</summary>
    public string[] Files { get; set; } = [];
}

/// <summary>Participant identity card (docs/MESSANGER.md §4).</summary>
public sealed class PigeonIdentity
{
    /// <summary>Session GUID for Playground harnesses; server-issued UUID for other clients.</summary>
    public string Id { get; set; } = "";
    /// <summary>Display name, unique, the only thing needed for @mentions.</summary>
    public string Alias { get; set; } = "";
    /// <summary>human | agent | bridge.</summary>
    public string Kind { get; set; } = "agent";
    public string Harness { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Self-reported "what I'm doing" — a living card, updated via pigeon_status.</summary>
    public string Status { get; set; } = "";
    /// <summary>
    /// Coarse activity state, self-reported on the heartbeat: idle | generating | dispatching |
    /// hung | errored. The server stores what it is told and draws no conclusions — "online"
    /// only says a heartbeat arrived, which is not the same as "this agent is awake and thinking".
    /// </summary>
    public string State { get; set; } = "";
    /// <summary>One line of what the state means right now: "tool: browser_navigate", "waiting for owner".</summary>
    public string Activity { get; set; } = "";
    /// <summary>When the state was last reported (states go stale: a "generating" from an hour ago is not a generating).</summary>
    public DateTimeOffset StateAt { get; set; }
    /// <summary>Wake capability: "mention" (push client) | "none" (pull client).</summary>
    public string Wake { get; set; } = "mention";
    /// <summary>Channel membership; Phase 0 validates exactly 1.</summary>
    public string[] Channels { get; set; } = [];
    /// <summary>Groups this participant joined of their own accord.</summary>
    public string[] Roles { get; set; } = [];
    /// <summary>Groups the owner put them in. Cannot be left by the participant.</summary>
    public string[] Assigned { get; set; } = [];
    /// <summary>
    /// Where this participant is connected from — e.g. "mcp#49c40ded@192.168.1.77".
    /// NOT part of identity: the identity outlives any one connection, and one identity may be
    /// reached from several addresses. Empty until a client reports one (MCP does, on every
    /// request; the harness does not). Answering "which box is that?" when the id cannot:
    /// <c>Store.Join</c> derives a disambiguated id from the host for clients that arrive
    /// without an identity.
    /// </summary>
    public string Connection { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>Presence: online = last heartbeat less than 45 s ago.</summary>
    public bool Online => (DateTimeOffset.UtcNow - LastSeen).TotalSeconds < 45;
}

/// <summary>Channel meta (docs/MESSANGER.md §5).</summary>
public sealed class PigeonChannel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"What is this channel for" — goes into members' state blocks.</summary>
    public string Description { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public bool Archived { get; set; }
    /// <summary>Consecutive agent messages ≥ threshold (safety net; off by default).</summary>
    public bool Hot { get; set; }
}

/// <summary>My unread info for a channel: cursor ("read up to id N") + count.</summary>
public sealed class PigeonUnread
{
    public string Channel { get; set; } = "";
    public long Since { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// One participant's reading position in one channel. Read receipts for the war room:
/// "did the agent see my message" is not guesswork when every identity carries a cursor.
/// </summary>
public sealed class PigeonCursor
{
    public string Identity { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Channel { get; set; } = "";
    /// <summary>Highest message id this identity has read (0 = nothing read).</summary>
    public long Since { get; set; }
    /// <summary>Highest message id in the channel, so the gap is visible without arithmetic.</summary>
    public long Head { get; set; }
    public int Unread { get; set; }
}

/// <summary>
/// A named subset of participants, so an address can reach a KIND of agent rather than a
/// specific one. Owner-managed; nobody is born into one. Replaces a magic @all: the owner
/// creates a group when they want everyone, and gives it a name and a description that say
/// what it is for.
/// </summary>
public sealed class PigeonGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"What is this group for" — the owner writes it so members can pick sensibly.</summary>
    public string Description { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    /// <summary>Members, for `who` and for showing a client what a name will reach.</summary>
    public string[] Members { get; set; } = [];
}

/// <summary>Self-service group membership change; the server knows who is asking.</summary>
public sealed class PigeonRoleChange
{
    public string Group { get; set; } = "";
}

/// <summary>Owner's edit of a channel's purpose. The description is read by every member, so it is configuration, not decoration.</summary>
public sealed class PigeonChannelDescribe
{
    public string Description { get; set; } = "";
}

/// <summary>Owner's rename of a channel. The id is stable (it is the slug the log and memberships point at); only the display name and optionally the description move.</summary>
public sealed class PigeonChannelRename
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

/// <summary>One entry in a file pool directory listing.</summary>
public sealed class PigeonFileEntry
{
    public string Name { get; set; } = "";
    public bool IsDir { get; set; }
    public long Size { get; set; }
    public string Owner { get; set; } = "";
    public DateTimeOffset Mtime { get; set; }
    public string Hash { get; set; } = "";
}

/// <summary>Result of a file put (create/overwrite).</summary>
public sealed class PigeonFileResult
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Hash { get; set; } = "";
}

/// <summary>Request body for a text file put.</summary>
public sealed class PigeonFilePut
{
    public string Content { get; set; } = "";
}

/// <summary>Request body for a server-side old→new edit.</summary>
public sealed class PigeonFileEdit
{
    public string OldText { get; set; } = "";
    public string NewText { get; set; } = "";
}

/// <summary>Response for a text file get.</summary>
public sealed class PigeonFileContent
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
    public long Size { get; set; }
    public string Owner { get; set; } = "";
    public string Hash { get; set; } = "";
}

/// <summary>Join request (idempotent; a persisted token re-authenticates).</summary>
public sealed class PigeonJoinRequest
{
    public string Id { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Kind { get; set; } = "agent";
    public string Harness { get; set; } = "";
    public string Model { get; set; } = "";
    public string Status { get; set; } = "";
    public string Wake { get; set; } = "mention";
    public string? Token { get; set; }
}

public sealed class PigeonJoinResult
{
    public string Token { get; set; } = "";
    /// <summary>A second live client with the same id (e.g. a copied client folder): flagged, not rejected.</summary>
    public bool Duplicate { get; set; }
}

public sealed class PigeonSendRequest
{
    public string Channel { get; set; } = "";
    public long? Thread { get; set; }
    public long? ReplyTo { get; set; }
    /// <summary>
    /// The message. Mentions live HERE as @tokens ("@alias" or "@guid") — the server
    /// resolves them to participant ids; there is no separate mentions field.
    /// </summary>
    public string Text { get; set; } = "";
    public string[] Files { get; set; } = [];
}
