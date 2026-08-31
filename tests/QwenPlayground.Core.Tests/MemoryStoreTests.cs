using System.IO;
using QwenPlayground.Core.Memory;

namespace QwenPlayground.Core.Tests;

public sealed class MemoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qpw_memtest_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Add_CreatesFileAndIndexLine()
    {
        var store = new MemoryStore(_dir);
        var item = store.Add("pointer-layout: запуск - единственный способ run/<id>");

        Assert.True(File.Exists(Path.Combine(_dir, item.Id + ".json")));
        Assert.Contains(item.Id, File.ReadAllText(store.IndexFile));
        // Без слоёв факт помечается в индексе как «без слоёв» (категорию назначает классификатор).
        Assert.Contains("без слоёв", File.ReadAllText(store.IndexFile));
    }

    [Fact]
    public void List_ReturnsItems_NewestFirst()
    {
        var store = new MemoryStore(_dir);
        store.Add("первый факт");
        Thread.Sleep(60); // CreatedAt — секундная точность, гарантируем порядок
        store.Add("второй факт");

        var items = store.List();

        Assert.Equal(2, items.Count);
        Assert.Equal("второй факт", items[0].Content);
    }

    [Fact]
    public void Get_ReturnsItem_OrNull()
    {
        var store = new MemoryStore(_dir);
        var item = store.Add("факт для Get");

        Assert.Equal(item.Content, store.Get(item.Id)?.Content);
        Assert.Null(store.Get("nonexistent"));
    }

    [Fact]
    public void Remove_DeletesFileAndRebuildsIndex()
    {
        var store = new MemoryStore(_dir);
        var item = store.Add("факт для удаления");

        Assert.True(store.Remove(item.Id));
        Assert.False(File.Exists(Path.Combine(_dir, item.Id + ".json")));
        Assert.DoesNotContain(item.Id, File.ReadAllText(store.IndexFile));
        Assert.False(store.Remove(item.Id)); // повторное удаление — false
    }

    [Fact]
    public void List_SkipsCorruptedFiles()
    {
        var store = new MemoryStore(_dir);
        store.Add("живой факт");
        File.WriteAllText(Path.Combine(_dir, "corrupt.json"), "{не json");

        var items = store.List();

        Assert.Single(items);
        Assert.Equal("живой факт", items[0].Content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
