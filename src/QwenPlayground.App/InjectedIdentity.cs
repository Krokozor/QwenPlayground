using System.IO;
using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.App;

/// <summary>
/// Динамический системный промпт main-агента: идентичность (main-agent.md) + слои памяти
/// (layers.json) + траектория (trajectory.md). В историю чата не пишется — собирается
/// при каждом рендере, чтобы переживать рестарты и rebuild_self.
///
/// Хот-путь: сборка случается на каждой итерации хода. Состав кэшируется по mtime
/// файлов-зависимостей — правка любого из них агентом через edit_file инвалидирует
/// кэш мгновенно, между правками файлы не перечитываются.
/// </summary>
public sealed class InjectedIdentity
{
    private readonly MemoryLayerStore _layerStore = new();
    private readonly FileDependentCache<string?> _cache;

    public InjectedIdentity()
    {
        _cache = new FileDependentCache<string?>(
            new[]
            {
                Path.Combine(SelfBuildPaths.WorkspaceRoot, MainAgent.IdentityFileName),
                _layerStore.FilePath,
                new TrajectoryStore().FilePath
            },
            Compose,
            initial: null);
    }

    /// <summary>Промпт для main-сессии; для остальных сессий null — динамическая идентичность только у агента.</summary>
    public string? GetFor(bool isMainSession) => isMainSession ? _cache.Get() : null;

    private string? Compose()
    {
        var parts = new List<string> { MainAgent.LoadIdentity(SelfBuildPaths.WorkspaceRoot) };
        var memoryBlock = _layerStore.Load().ToPromptBlock();
        if (memoryBlock.Length > 0)
        {
            parts.Add(memoryBlock);
        }
        var trajectory = new TrajectoryStore().Load();
        if (trajectory.Length > 0)
        {
            parts.Add("— Траектория (текущее направление) —\n" + trajectory);
        }
        return string.Join("\n\n", parts);
    }
}
