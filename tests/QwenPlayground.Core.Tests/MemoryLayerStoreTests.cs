using System.IO;
using QwenPlayground.Core.Memory;

namespace QwenPlayground.Core.Tests;

public sealed class MemoryLayerStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qpw_layers_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveLoad_RoundTripsAllLayers()
    {
        var store = new MemoryLayerStore(_dir);
        var layers = new LayerMemory { L1 = "старый дистиллят", L2 = "средний", L3 = "свежий" };

        store.Save(layers);
        var loaded = store.Load();

        Assert.Equal("старый дистиллят", loaded.L1);
        Assert.Equal("средний", loaded.L2);
        Assert.Equal("свежий", loaded.L3);
    }

    [Fact]
    public void Load_WithoutFile_ReturnsEmpty()
    {
        var loaded = new MemoryLayerStore(_dir).Load();

        Assert.True(loaded.IsEmpty);
    }

    [Fact]
    public void Load_CorruptedFile_ReturnsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "layers.json"), "{не json");

        var loaded = new MemoryLayerStore(_dir).Load();

        Assert.True(loaded.IsEmpty);
    }

    [Fact]
    public void ToPromptBlock_SkipsEmptyLayers()
    {
        var layers = new LayerMemory { L1 = "только старый слой" };
        var block = layers.ToPromptBlock();

        Assert.Contains("Слой L1", block);
        Assert.Contains("только старый слой", block);
        Assert.DoesNotContain("Слой L2", block);
        Assert.DoesNotContain("Слой L3", block);
    }

    [Fact]
    public void ToPromptBlock_EmptyMemory_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, new LayerMemory().ToPromptBlock());
    }

    [Fact]
    public void ToPromptBlock_ContainsExplanationForTheModel()
    {
        var block = new LayerMemory { L3 = "свежие события" }.ToPromptBlock();

        Assert.Contains("слоистая память", block);
        Assert.Contains("L1", block); // объяснение упоминает глубину слоёв
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}