using System.IO;
using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.Tests;

public sealed class ContextBackupStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _sessionsRoot;
    private readonly string _backupsRoot;
    private readonly ContextBackupStore _store;

    public ContextBackupStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qpw_bk_" + Guid.NewGuid().ToString("N"));
        _sessionsRoot = Path.Combine(_root, "sessions");
        _backupsRoot = Path.Combine(_root, "backups");
        Directory.CreateDirectory(_sessionsRoot);
        _store = new ContextBackupStore(_sessionsRoot, _backupsRoot);
    }

    [Fact]
    public void Save_CopiesSessionFile_Verbatim()
    {
        File.WriteAllText(Path.Combine(_sessionsRoot, "s1.json"), "hello-session");

        var path = _store.Save("s1");

        Assert.True(File.Exists(path));
        Assert.StartsWith("s1-", Path.GetFileName(path));
        Assert.EndsWith(".json", path);
        Assert.Equal("hello-session", File.ReadAllText(path)); // побайтово, без пере-сериализации
    }

    [Fact]
    public void Save_CopiesSessionFolder_Verbatim()
    {
        Directory.CreateDirectory(Path.Combine(_sessionsRoot, "main"));
        File.WriteAllText(Path.Combine(_sessionsRoot, "main", "chat.json"), "chat-data");
        File.WriteAllText(Path.Combine(_sessionsRoot, "main", "layers.json"), "layers-data");

        var path = _store.Save("main");

        Assert.True(Directory.Exists(path));
        Assert.Equal("chat-data", File.ReadAllText(Path.Combine(path, "chat.json")));
        Assert.Equal("layers-data", File.ReadAllText(Path.Combine(path, "layers.json")));
    }

    [Fact]
    public void Save_Throws_WhenSessionMissing()
    {
        Assert.Throws<FileNotFoundException>(() => _store.Save("nope"));
    }

    [Fact]
    public void Restore_PutsFilesBack()
    {
        File.WriteAllText(Path.Combine(_sessionsRoot, "s2.json"), "original");

        var backup = _store.Save("s2");
        File.Delete(Path.Combine(_sessionsRoot, "s2.json")); // «потеряли» сессию
        File.WriteAllText(Path.Combine(_sessionsRoot, "s2.json"), "tampered");

        var restored = _store.Restore(backup);

        Assert.Equal("original", File.ReadAllText(restored));
    }

    [Fact]
    public void Restore_ReplacesWholeFolder()
    {
        Directory.CreateDirectory(Path.Combine(_sessionsRoot, "main"));
        File.WriteAllText(Path.Combine(_sessionsRoot, "main", "chat.json"), "v1");

        var backup = _store.Save("main");
        File.WriteAllText(Path.Combine(_sessionsRoot, "main", "chat.json"), "v2");
        File.WriteAllText(Path.Combine(_sessionsRoot, "main", "stale_extra.json"), "должно исчезнуть");

        var restored = _store.Restore(backup);

        Assert.Equal(Path.Combine(_sessionsRoot, "main"), restored);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(_sessionsRoot, "main", "chat.json")));
        Assert.False(File.Exists(Path.Combine(_sessionsRoot, "main", "stale_extra.json")));
    }

    [Fact]
    public void GC_KeepsOnlyLastFive()
    {
        // Имена сортируются по времени: новейшие остаются, старые удаляются.
        for (var i = 1; i <= 7; i++)
        {
            var name = $"s3-20260819-12000{i}";
            File.WriteAllText(Path.Combine(_backupsRoot, name + ".json"), i.ToString());
        }

        _store.GC("s3");

        var remaining = _store.List("s3");
        Assert.Equal(ContextBackupStore.KeepLast, remaining.Count);
        Assert.All(remaining, p => Assert.DoesNotContain("120001", p));
        Assert.All(remaining, p => Assert.DoesNotContain("120002", p));
    }

    [Fact]
    public void List_ReturnsNewestFirst()
    {
        File.WriteAllText(Path.Combine(_backupsRoot, "s4-20260819-110000.json"), "older");
        File.WriteAllText(Path.Combine(_backupsRoot, "s4-20260819-120000.json"), "newer");

        var list = _store.List("s4");

        Assert.Equal(2, list.Count);
        Assert.Contains("110000", list[1]);
        Assert.Contains("120000", list[0]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}