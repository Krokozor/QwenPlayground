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

    [Fact]
    public void Delete_RemovesSessionFolderFromDisk()
    {
        _store.Save("s1", new List<ChatMessage> { ChatMessage.User("вопрос") });
        File.WriteAllText(Path.Combine(_directory, "s1", "draft.txt"), "черновик");
        Directory.CreateDirectory(Path.Combine(_directory, "s1", "artifacts", "msg_1"));
        var folder = Path.Combine(_directory, "s1");
        Assert.True(Directory.Exists(folder));

        _store.Delete("s1");

        Assert.False(Directory.Exists(folder)); // ни файлов, ни папки — ничего не осталось
    }

    [Fact]
    public void PruneEmptyFolders_RemovesEmpty_KeepsNonEmptyAndProtected()
    {
        // Непустая сессия (есть chat.json) — трогать нельзя.
        _store.Save("full", new List<ChatMessage> { ChatMessage.User("x") });
        // Каталог без chat.json, но с сайдкаром (счётчик) — данные сессии, не мусор.
        Directory.CreateDirectory(Path.Combine(_directory, "counter-only"));
        File.WriteAllText(Path.Combine(_directory, "counter-only", "counter"), "7");
        // Пустой каталог — мусор (сессия удалена/не сохранялась).
        Directory.CreateDirectory(Path.Combine(_directory, "orphan"));
        // main пуст законно — он в keep.
        Directory.CreateDirectory(Path.Combine(_directory, "main"));

        var removed = _store.PruneEmptyFolders(["main"]);

        Assert.Equal(["orphan"], removed);
        Assert.False(Directory.Exists(Path.Combine(_directory, "orphan")));
        Assert.True(Directory.Exists(Path.Combine(_directory, "main")));
        Assert.True(Directory.Exists(Path.Combine(_directory, "full")));
        Assert.True(Directory.Exists(Path.Combine(_directory, "counter-only")));
        Assert.Single(_store.List()); // полная сессия по-прежнему в списке
    }

    [Fact]
    public void PruneEmptyFolders_DropsIndexEntriesWithoutDataOnDisk()
    {
        _store.Save("alive", new List<ChatMessage> { ChatMessage.User("x") });
        _store.Save("gone", new List<ChatMessage> { ChatMessage.User("y") });
        Directory.Delete(Path.Combine(_directory, "gone"), recursive: true);

        _store.PruneEmptyFolders();

        // Мёртвая запись индекса вычищена, живая на месте.
        Assert.DoesNotContain("\"gone\"", File.ReadAllText(Path.Combine(_directory, "index.json")));
        Assert.Contains("\"alive\"", File.ReadAllText(Path.Combine(_directory, "index.json")));
        Assert.Equal("alive", _store.List()[0].Id);
    }
}

/// <summary>
/// Копии вложений в artifacts/msg_&lt;id&gt;/. Регресс: одноимённые вложения раньше
/// перетирали друг друга (overwrite: true) — в UI было два чипа, у модели одна картинка.
/// </summary>
public sealed class MessageMetaStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "qwen_artifacts_" + Guid.NewGuid().ToString("N"));

    public MessageMetaStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Ничего не создавали — удалять нечего.
        }
    }

    private string Source(string name, string content)
    {
        var path = Path.Combine(_directory, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void AddArtifact_SameBasename_KeepsBothCopies()
    {
        var store = new MessageMetaStore(_directory);

        var first = store.AddArtifact(1, Source("a/shot.png", "first"));
        var second = store.AddArtifact(1, Source("b/shot.png", "second"));

        Assert.NotEqual(first, second);
        Assert.Equal(2, store.GetArtifacts(1).Count);
        Assert.Equal("first", File.ReadAllText(first));
        Assert.Equal("second", File.ReadAllText(second));
    }

    [Fact]
    public void AddFileArtifact_SameBasename_KeepsBothCopies()
    {
        var store = new MessageMetaStore(_directory);

        var first = store.AddFileArtifact(1, Source("a/doc.txt", "first"));
        var second = store.AddFileArtifact(1, Source("b/doc.txt", "second"));

        Assert.NotEqual(first, second);
        Assert.Equal("first", File.ReadAllText(first));
        Assert.Equal("second", File.ReadAllText(second));
    }

    [Fact]
    public void AddArtifact_UniqueName_KeepsOriginalName()
    {
        var store = new MessageMetaStore(_directory);

        var path = store.AddArtifact(1, Source("screenshot.png", "bytes"));

        Assert.Equal("screenshot.png", Path.GetFileName(path));
        Assert.Equal([path], store.GetArtifacts(1));
    }

    [Fact]
    public void RemoveArtifacts_DropsFolder()
    {
        var store = new MessageMetaStore(_directory);
        store.AddArtifact(1, Source("shot.png", "bytes"));

        Assert.Equal(1, store.RemoveArtifacts(1));
        Assert.Empty(store.GetArtifacts(1));
    }
}
