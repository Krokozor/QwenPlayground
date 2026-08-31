using System.Text.Json;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.Memory;

/// <summary>
/// Хранилище слоёв памяти (L1/L2/L3) main-агента: файл layers.json в каталоге сессии.
/// Источник истины для долгосрочной памяти — в сам чат слои не пишутся, а инжектятся
/// в системный промпт при сборке (см. LayerMemory.ToPromptBlock).
/// </summary>
public sealed class MemoryLayerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;

    public MemoryLayerStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(SelfBuildPaths.WorkspaceRoot, "sessions", "main");
        System.IO.Directory.CreateDirectory(_directory);
    }

    /// <summary>Каталог сессии (sessions/main).</summary>
    public string Directory => _directory;

    public string FilePath => Path.Combine(_directory, "layers.json");

    public LayerMemory Load()
    {
        if (!File.Exists(FilePath))
        {
            return new LayerMemory();
        }
        try
        {
            return JsonSerializer.Deserialize<LayerMemory>(File.ReadAllText(FilePath)) ?? new LayerMemory();
        }
        catch (JsonException)
        {
            return new LayerMemory();
        }
    }

    public void Save(LayerMemory memory) =>
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(memory, JsonOptions));
}