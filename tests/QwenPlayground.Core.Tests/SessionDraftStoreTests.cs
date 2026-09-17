using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.Tests;

public sealed class SessionDraftStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly SessionDraftStore _store;

    public SessionDraftStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "qwen_drafts_" + Guid.NewGuid().ToString("N"));
        _store = new SessionDraftStore(_directory);
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
    public void SaveLoad_RoundTripsText()
    {
        _store.Save("main", "большой набранный промпт\nсо строками");

        var loaded = _store.Load("main");
        Assert.Equal("большой набранный промпт\nсо строками", loaded);
        Assert.True(_store.Exists("main"));
    }

    [Fact]
    public void Save_EmptyText_CreatesNoFile()
    {
        _store.Save("main", string.Empty);

        Assert.Null(_store.Load("main"));
        Assert.False(_store.Exists("main"));
    }

    [Fact]
    public void Load_MissingSession_ReturnsNull()
    {
        Assert.Null(_store.Load("never-saved"));
        Assert.False(_store.Exists("never-saved"));
    }

    [Fact]
    public void Clear_RemovesDraft()
    {
        _store.Save("main", "текст");
        Assert.True(_store.Exists("main"));

        _store.Clear("main");
        Assert.False(_store.Exists("main"));
        Assert.Null(_store.Load("main"));
    }

    [Fact]
    public void Clear_MissingSession_IsNoOp()
    {
        _store.Clear("never-saved"); // не бросает
        Assert.False(_store.Exists("never-saved"));
    }

    [Fact]
    public void Drafts_ArePerSession_Isolated()
    {
        _store.Save("session-a", "черновик A");
        _store.Save("session-b", "черновик B");

        Assert.Equal("черновик A", _store.Load("session-a"));
        Assert.Equal("черновик B", _store.Load("session-b"));

        _store.Clear("session-a");
        Assert.Null(_store.Load("session-a"));
        Assert.Equal("черновик B", _store.Load("session-b")); // B не задет
    }

    [Fact]
    public void Save_OverwritesPreviousDraft()
    {
        _store.Save("main", "первая версия");
        _store.Save("main", "вторая версия, длиннее");

        Assert.Equal("вторая версия, длиннее", _store.Load("main"));
    }
}
