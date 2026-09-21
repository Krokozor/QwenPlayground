using System.IO;

using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// SessionController на изолированном каталоге: инварианты переключения сессий —
/// flush драфта старой → загрузка → restore драфта новой; очистка транзитного
/// состояния (surfaced-пул) при смене; ключи профилей (сброс на новой/удалении
/// текущей, персистенция в SessionData); событие SessionChanged.
/// Настройки (LastSessionId, MemoryEnabled) — глобальный синглтон: бэкап и возврат.
/// Коллекция memory-settings: классы, мутирующие MemoryEnabled, идут последовательно
/// (xUnit крутит классы параллельно — бэкап/возврат глобальной настройки без
/// сериализации гонится: чужой транзитный false закрепляется как «окружение»).
/// </summary>
[Collection("memory-settings")]
public sealed class SessionControllerTests : IDisposable
{
    private readonly string _root;
    private readonly string? _savedLastSessionId;
    private readonly bool _savedMemoryEnabled;
    private readonly ChatLog _log = new();
    private string _input = string.Empty;
    private readonly DraftKeeper _draft;
    private readonly MemorySurfacer _surfacer = new();
    private readonly SessionController _controller;
    private int _sessionChangedCount;

    public SessionControllerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qpw_sessionctl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _savedLastSessionId = AppSettings.Get().LastSessionId;
        _savedMemoryEnabled = AppSettings.Get().MemoryEnabled;
        _draft = new DraftKeeper(
            () => _input,
            text => _input = text,
            () => _controller!.CurrentId,
            new SessionDraftStore(_root),
            () => 15);
        _controller = new SessionController(_log, _draft, _surfacer, _root);
        _controller.SessionChanged += () => _sessionChangedCount++;
    }

    public void Dispose()
    {
        AppSettings.Get().LastSessionId = _savedLastSessionId; // не протекаем в другие тесты
        AppSettings.Get().MemoryEnabled = _savedMemoryEnabled;
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void EnsureMain_CreatesSessionAndPersists()
    {
        _log.Add(ChatMessage.User("привет"));
        _controller.EnsureMain();

        Assert.Equal(MainAgent.SessionId, _controller.CurrentId);
        Assert.True(File.Exists(Path.Combine(_root, MainAgent.SessionId, "chat.json")));
    }

    [Fact]
    public void Load_SwitchesDrafts_FlushesOldAndRestoresNew()
    {
        _controller.EnsureMain();
        _input = "черновик main";
        _draft.Flush();

        _controller.StartNew();
        _controller.SaveCurrent();
        var secondId = _controller.CurrentId;

        _input = "черновик второй";
        _draft.Flush();

        _controller.Load(MainAgent.SessionId);

        Assert.Equal("черновик main", _input);
    }

    [Fact]
    public void Load_RestoresDraftOfSwitchedToSession()
    {
        _controller.EnsureMain();
        _controller.StartNew();
        _controller.SaveCurrent();
        var secondId = _controller.CurrentId;

        _input = "черновик второй";
        _draft.Flush();

        _controller.Load(MainAgent.SessionId);
        _input = string.Empty;
        _controller.Load(secondId);

        Assert.Equal("черновик второй", _input);
    }

    [Fact]
    public void Load_ClearsSurfacedMemory()
    {
        AppSettings.Get().MemoryEnabled = true;
        _controller.EnsureMain();
        _controller.StartNew();
        _controller.SaveCurrent();
        var secondId = _controller.CurrentId;

        _surfacer.SurfaceOwnWrite("fact-1", "факт");
        Assert.NotEmpty(_surfacer.GetSurfacedForStateBlock());

        _controller.Load(secondId);

        Assert.Empty(_surfacer.GetSurfacedForStateBlock());
    }

    [Fact]
    public void Load_UnknownSession_ReturnsFalseAndKeepsCurrent()
    {
        _controller.EnsureMain();

        Assert.False(_controller.Load("no-such-session"));
        Assert.Equal(MainAgent.SessionId, _controller.CurrentId);
    }

    [Fact]
    public void Load_RaisesSessionChanged()
    {
        _controller.EnsureMain();
        _controller.StartNew();
        _controller.SaveCurrent();
        var secondId = _controller.CurrentId;
        var changes = _sessionChangedCount;

        _controller.Load(secondId);

        Assert.Equal(changes + 1, _sessionChangedCount);
    }

    [Fact]
    public void StartNew_ResetsProfileKeysAndRaisesEvent()
    {
        _controller.EnsureMain();
        _controller.ApplyProfileKeys("sampler-x", "prompt-y", "state-z");
        Assert.Equal("sampler-x", _controller.SamplerKey);

        var changes = _sessionChangedCount;
        _controller.StartNew();

        Assert.Null(_controller.SamplerKey);
        Assert.Null(_controller.PromptKey);
        Assert.Null(_controller.StateBlockKey);
        Assert.Equal(changes + 1, _sessionChangedCount);
    }

    [Fact]
    public void Delete_CurrentSession_ClearsLogAndResetsKeys()
    {
        _controller.EnsureMain();
        _controller.StartNew();
        _controller.SaveCurrent();
        var secondId = _controller.CurrentId;
        _log.Add(ChatMessage.User("сообщение"));
        _controller.ApplyProfileKeys("s", "p", "sb");

        var deletedCurrent = _controller.Delete(secondId);

        Assert.True(deletedCurrent);
        Assert.NotEqual(secondId, _controller.CurrentId);
        Assert.Empty(_log);
        Assert.Null(_controller.SamplerKey);
    }

    [Fact]
    public void Delete_NonCurrentSession_KeepsLogAndKeys()
    {
        _controller.EnsureMain();
        _controller.StartNew();
        _controller.SaveCurrent();
        var firstOther = _controller.CurrentId;
        _controller.StartNew();
        _controller.SaveCurrent();
        _log.Add(ChatMessage.User("остаётся"));
        _controller.ApplyProfileKeys("s", "p", "sb");

        var deletedCurrent = _controller.Delete(firstOther);

        Assert.False(deletedCurrent);
        Assert.Single(_log);
        Assert.Equal("s", _controller.SamplerKey);
    }

    [Fact]
    public void ApplyProfileKeys_PersistsToSessionData()
    {
        _controller.EnsureMain();
        _controller.ApplyProfileKeys("sampler-x", "prompt-y", "state-z");

        var data = new SessionStore(_root).Load(MainAgent.SessionId);
        Assert.Equal("sampler-x", data?.SamplerKey);
        Assert.Equal("prompt-y", data?.PromptKey);
        Assert.Equal("state-z", data?.StateBlockKey);
    }

    [Fact]
    public void Load_RestoresProfileKeysFromSessionData()
    {
        _controller.EnsureMain();
        _controller.ApplyProfileKeys("sampler-x", "prompt-y", "state-z");
        _controller.StartNew();
        _controller.SaveCurrent();
        var secondId = _controller.CurrentId;

        _controller.Load(MainAgent.SessionId);

        Assert.Equal("sampler-x", _controller.SamplerKey);
        Assert.Equal("prompt-y", _controller.PromptKey);
        Assert.Equal("state-z", _controller.StateBlockKey);
    }

    [Fact]
    public void CreateDetached_ReturnsNewId_WithoutTouchingCurrentOrLast()
    {
        _controller.EnsureMain();

        var id = _controller.CreateDetached();

        Assert.NotEqual(MainAgent.SessionId, id);
        Assert.Equal(MainAgent.SessionId, _controller.CurrentId);
        Assert.Equal(_savedLastSessionId, AppSettings.Get().LastSessionId);
    }

    [Fact]
    public void SavePinned_WritesPinnedSession_WithoutTouchingCurrent()
    {
        _controller.EnsureMain();
        var pinnedId = _controller.CreateDetached();
        var pinnedLog = new ChatLog();
        pinnedLog.Add(new ChatMessage { Role = ChatRole.User, Content = "привет из закреплённого окна" });

        _controller.SavePinned(pinnedId, pinnedLog, samplerKey: "s", promptKey: "p", stateBlockKey: "b");

        var data = new SessionStore(_root).Load(pinnedId);
        Assert.NotNull(data);
        Assert.Single(data!.Messages);
        Assert.Equal("s", data.SamplerKey);
        Assert.Equal("p", data.PromptKey);
        Assert.Equal("b", data.StateBlockKey);
        // Текущая сессия не задета.
        Assert.Equal(MainAgent.SessionId, _controller.CurrentId);
        Assert.Equal(_savedLastSessionId, AppSettings.Get().LastSessionId);
    }
}
