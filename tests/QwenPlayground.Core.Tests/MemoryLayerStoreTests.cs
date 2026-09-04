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

        Assert.Contains("Layer L1", block);
        Assert.Contains("только старый слой", block);
        Assert.DoesNotContain("Layer L2", block);
        Assert.DoesNotContain("Layer L3", block);
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

        Assert.Contains("layered memory", block);
        Assert.Contains("L1", block); // the explanation mentions the layer depth
    }

    [Fact]
    public void ToPromptBlock_UsesMarkdownHeadingsLikeToolsSection()
    {
        // Форматирование системного промпта едино: секция — H1, слой — H2 (стиль «# Tools»).
        var layers = new LayerMemory { L1 = "старое", L3 = "новое" };
        var block = layers.ToPromptBlock();

        Assert.StartsWith("# Long-term memory (layers L1–L3)", block);
        Assert.Contains("## Layer L1", block);
        Assert.Contains("## Layer L3", block);
        Assert.DoesNotContain("## Layer L2", block);
        // H2 заголовок отделён от содержимого пустой строкой, как «# Tools» (AppendLine → CRLF).
        var nl = Environment.NewLine;
        Assert.Contains($"## Layer L1{nl}{nl}старое", block);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}