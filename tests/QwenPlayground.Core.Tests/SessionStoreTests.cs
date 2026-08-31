using QwenPlayground.Core.Chat;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly SessionStore _store;

    public SessionStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "qwen_sessions_" + Guid.NewGuid().ToString("N"));
        _store = new SessionStore(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void SaveLoad_RoundTripsFullConversation()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            ChatMessage.User("hello there"),
            new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = "answer",
                Reasoning = "thinking",
                ThinkingClosed = true,
                ToolCalls = new List<ToolCall>
                {
                    new() { Name = "read_file", Arguments = System.Text.Json.Nodes.JsonNode.Parse("""{"path":"a.txt"}""")! }
                },
                Generation = new GenerationInfo { Prompt = "P", RawOutput = "R", PromptTokens = 10, CompletionTokens = 5 }
            },
            ChatMessage.Tool("result")
        };

        _store.Save("s1", messages);
        var loaded = _store.Load("s1");

        Assert.NotNull(loaded);
        Assert.Equal(4, loaded.Messages.Count);
        Assert.Equal("chat", loaded!.Purpose); // дефолтная цель
        Assert.Null(loaded.SamplerKey); // куски профиля не назначены — default
        Assert.Null(loaded.PromptKey);
        Assert.Null(loaded.StateBlockKey);

        // Явная цель (типизированные сессии будущего) проходит сквозь хранилище.
        _store.Save("s1", messages, purpose: "research");
        Assert.Equal("research", _store.Load("s1")!.Purpose);

        // Куски профиля персистятся независимо: семплер один, промпт другой, state — третий.
        _store.Save("s1", messages, samplerKey: "cold", promptKey: "shaders", stateBlockKey: "quiet");
        var withKeys = _store.Load("s1")!;
        Assert.Equal("cold", withKeys.SamplerKey);
        Assert.Equal("shaders", withKeys.PromptKey);
        Assert.Equal("quiet", withKeys.StateBlockKey);

        var assistant = loaded.Messages[2];
        Assert.Equal("answer", assistant.Content);
        Assert.Equal("thinking", assistant.Reasoning);
        Assert.True(assistant.ThinkingClosed);
        Assert.Equal("read_file", assistant.ToolCalls![0].Name);
        Assert.Equal("a.txt", assistant.ToolCalls[0].Arguments["path"]!.GetValue<string>());
        Assert.Equal("P", assistant.Generation!.Prompt);
        Assert.Equal(10, assistant.Generation.PromptTokens);

        Assert.Equal("hello there", loaded.Title);
        Assert.Equal(SessionStore.CurrentFormatVersion, loaded.FormatVersion);
    }

    [Fact]
    public void Load_LegacyFileWithoutVersion_ReadsWithZero()
    {
        // Файл до введения версионирования: поля нет → 0 (сигнал для будущих миграций).
        Directory.CreateDirectory(Path.Combine(_directory, "legacy"));
        File.WriteAllText(
            Path.Combine(_directory, "legacy", "chat.json"),
            """{"Id":"legacy","Title":"","UpdatedAt":"2026-01-01T00:00:00","Messages":[],"NextMessageId":3}""");

        var loaded = _store.Load("legacy");

        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.FormatVersion);
        Assert.Equal(3, loaded.NextMessageId);
        Assert.Equal("chat", loaded.Purpose); // поле отсутствовало — инициализатор дал дефолт
    }

    [Fact]
    public void SaveLoad_RoundTripsStateBlockAsObject()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = "answer",
                Reasoning = "thinking",
                ThinkingClosed = true,
                StateBlock = new StateBlock
                {
                    MsgId = 7,
                    Time = new DateTime(2026, 8, 17, 13, 15, 26),
                    ContextUsed = 12345,
                    ContextMax = 32768,
                    BuildId = "20260817-102607",
                    BuildStatus = "success",
                    Memories = { new StateBlock.MemoryRef { Id = "mem1", Relevance = 0.95, Content = "fact" } },
                    MemoryNag = "do memory management",
                    Nag = "call sanity_check"
                }
            }
        };

        _store.Save("s2", messages);
        var loaded = _store.Load("s2");

        Assert.NotNull(loaded);
        var state = loaded.Messages[1].StateBlock;
        Assert.NotNull(state);
        Assert.Equal(7, state.MsgId);
        Assert.Equal(new DateTime(2026, 8, 17, 13, 15, 26), state.Time);
        Assert.Equal(12345, state.ContextUsed);
        Assert.Equal(32768, state.ContextMax);
        Assert.Equal("20260817-102607", state.BuildId);
        Assert.Equal("success", state.BuildStatus);
        Assert.Single(state.Memories);
        Assert.Equal("mem1", state.Memories[0].Id);
        Assert.Equal(0.95, state.Memories[0].Relevance);
        Assert.Equal("fact", state.Memories[0].Content);
        Assert.Equal("do memory management", state.MemoryNag);
        Assert.Equal("call sanity_check", state.Nag);
    }

    [Fact]
    public void List_OrderedByUpdatedDesc_AndDelete()
    {
        _store.Save("a", new List<ChatMessage> { ChatMessage.User("first") });
        // Таймер Windows ~15.6 мс: 60 мс гарантированно дают разные UpdatedAt.
        Thread.Sleep(60);
        _store.Save("b", new List<ChatMessage> { ChatMessage.User("second") });

        var list = _store.List();
        Assert.Equal(2, list.Count);
        Assert.Equal("b", list[0].Id);

        _store.Delete("a");
        Assert.Single(_store.List());
        Assert.Null(_store.Load("a"));
    }

    [Fact]
    public void List_IgnoresCorruptFiles()
    {
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "not json {");

        Assert.Empty(_store.List());
    }
}
