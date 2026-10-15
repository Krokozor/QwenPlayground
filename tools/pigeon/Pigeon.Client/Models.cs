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
    /// <summary>Mentioned participant ids (explicit, not text-parsed).</summary>
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
    /// <summary>Wake capability: "mention" (push client) | "none" (pull client).</summary>
    public string Wake { get; set; } = "mention";
    /// <summary>Channel membership; Phase 0 validates exactly 1.</summary>
    public string[] Channels { get; set; } = [];
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
    public string Text { get; set; } = "";
    /// <summary>Participant ids (or @aliases — the server resolves both).</summary>
    public string[] Mentions { get; set; } = [];
    public string[] Files { get; set; } = [];
}
