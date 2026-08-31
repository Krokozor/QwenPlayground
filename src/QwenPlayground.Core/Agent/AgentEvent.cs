using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;

namespace QwenPlayground.Core.Agent;

public abstract record AgentEvent;

public sealed record TokenEvent(string Text) : AgentEvent;

public sealed record AssistantMessageEvent(ChatMessage Message) : AgentEvent;

public sealed record ToolCallStartedEvent(string Name, JsonObject Arguments) : AgentEvent;

public sealed record ToolCallFinishedEvent(string Name, string Result, ChatMessage ToolMessage) : AgentEvent;

public sealed record AgentDoneEvent : AgentEvent;

public sealed record RestartPendingEvent : AgentEvent;

public sealed record NagEvent(string Text) : AgentEvent;

public sealed record AgentErrorEvent(string Message) : AgentEvent;
