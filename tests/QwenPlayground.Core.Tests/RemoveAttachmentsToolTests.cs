using System.Text.Json.Nodes;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// remove_attachments: очистка вложений из одного или нескольких сообщений
/// (массив msg_ids — одной командой), дубликаты считаются один раз,
/// отсутствие вложений — не ошибка, пустой список ID — сообщение.
/// </summary>
public sealed class RemoveAttachmentsToolTests : IDisposable
{
    private readonly string _root;
    private readonly string _sessionDir;
    private readonly ToolRegistry _registry = new();
    private readonly ToolContext _context;

    public RemoveAttachmentsToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qwen_remove_attachments_" + Guid.NewGuid().ToString("N"));
        _sessionDir = Path.Combine(_root, "sessions", "test");
        Directory.CreateDirectory(_sessionDir);
        _context = new ToolContext(_root, sessionDir: _sessionDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private static JsonObject Ids(params int[] ids)
    {
        var array = new JsonArray();
        foreach (var id in ids)
        {
            array.Add(id);
        }
        return new JsonObject { ["msg_ids"] = array };
    }

    private string SourceFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "bytes");
        return path;
    }

    [Fact]
    public async Task SingleId_RemovesAttachments()
    {
        var store = new MessageMetaStore(_sessionDir);
        store.AddArtifact(10, SourceFile("a.png"));

        var result = await _registry.ExecuteAsync("remove_attachments", Ids(10), _context);

        Assert.Contains("Removed 1 attachment", result);
        Assert.Empty(store.GetArtifacts(10));
    }

    [Fact]
    public async Task MultipleIds_RemovesFromAll()
    {
        var store = new MessageMetaStore(_sessionDir);
        store.AddArtifact(10, SourceFile("a.png"));
        store.AddArtifact(20, SourceFile("b.png"));
        store.AddArtifact(20, SourceFile("c.png"));
        store.AddArtifact(30, SourceFile("d.png"));

        var result = await _registry.ExecuteAsync("remove_attachments", Ids(10, 20, 30), _context);

        Assert.Contains("Removed 4 attachment(s)", result);
        Assert.Empty(store.GetArtifacts(10));
        Assert.Empty(store.GetArtifacts(20));
        Assert.Empty(store.GetArtifacts(30));
    }

    [Fact]
    public async Task DuplicateIds_CountedOnce()
    {
        var store = new MessageMetaStore(_sessionDir);
        store.AddArtifact(10, SourceFile("a.png"));

        var result = await _registry.ExecuteAsync("remove_attachments", Ids(10, 10), _context);

        Assert.Contains("Removed 1 attachment", result);
        Assert.Empty(store.GetArtifacts(10));
    }

    [Fact]
    public async Task NoAttachments_NotAnError()
    {
        var result = await _registry.ExecuteAsync("remove_attachments", Ids(99), _context);

        Assert.Contains("No attachments found", result);
    }

    [Fact]
    public async Task EmptyIds_ReturnsMessage()
    {
        var result = await _registry.ExecuteAsync("remove_attachments", Ids(), _context);

        Assert.Contains("No message IDs provided", result);
    }
}
