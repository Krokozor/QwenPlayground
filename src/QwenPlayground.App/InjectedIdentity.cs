using System.IO;
using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.App;

/// <summary>
/// Динамическое ЯДРО системного промпта main-агента: идентичность (main-agent.md) +
/// траектория (trajectory.md). Слои памяти НЕ здесь — их дописывает
/// MainViewModel.ResolveSystemPrompt в самый конец промпта (общий порядок секций у main
/// и не-main). В историю чата не пишется — собирается при каждом рендере, чтобы
/// переживать рестарты и rebuild_self.
///
/// Хот-путь: сборка случается на каждой итерации хода. Состав кэшируется по mtime
/// файлов-зависимостей — правка любого из них агентом через edit_file инвалидирует
/// кэш мгновенно, между правками файлы не перечитываются.
/// </summary>
public sealed class InjectedIdentity
{
    private readonly FileDependentCache<string?> _cache;

    public InjectedIdentity()
    {
        _cache = new FileDependentCache<string?>(
            new[]
            {
                Path.Combine(SelfBuildPaths.WorkspaceRoot, MainAgent.IdentityFileName),
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
        var trajectory = new TrajectoryStore().Load();
        if (trajectory.Length > 0)
        {
            parts.Add("# Trajectory (current direction)\n\n" + trajectory);
        }
        return string.Join("\n\n", parts);
    }
}
