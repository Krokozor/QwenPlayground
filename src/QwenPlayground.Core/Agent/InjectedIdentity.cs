using System.IO;
using QwenPlayground.Core.Serialization;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Agent;

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
                new TrajectoryStore().FilePath,
                new ProjectNotebookStore().FilePath
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
        // Записная книжка (refactoring.md) — источник «незакрытых пунктов» для heartbeat'а:
        // инжектим её хвост (backlog + свежий changelog), а не весь файл (сотни строк истории).
        // Load() при отсутствии файла создаёт его из нейтрального шаблона — ссылки в промптах
        // (identity/heartbeat) всегда ведут на существующий файл, а не на фантома.
        var notebook = new ProjectNotebookStore().Load();
        if (notebook.Length > 0)
        {
            parts.Add("# Project notebook (backlog tail)\n\n" + NotebookTail(notebook));
        }
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Хвост записной книжки: от ПОСЛЕДНЕГО «## »-заголовка, содержащего «backlog»
    /// (регистронезависимо — в шаблоне «## Backlog», в записной книжке владельца, например,
    /// «## Идеи развития (backlog)»), до конца файла: незакрытые пункты — то, что heartbeat
    /// ищет. Без такого заголовка (файл с другой структурой) — последние ~2000 символов:
    /// свежий changelog ближе к концу, чем принципы в шапке.
    /// </summary>
    private static string NotebookTail(string notebook)
    {
        var start = -1;
        var searchFrom = 0;
        while (true)
        {
            var heading = notebook.IndexOf("\n## ", searchFrom, StringComparison.Ordinal);
            if (heading < 0)
            {
                break;
            }
            var lineEnd = notebook.IndexOf('\n', heading + 1);
            if (lineEnd < 0)
            {
                lineEnd = notebook.Length;
            }
            if (notebook.AsSpan(heading, lineEnd - heading)
                    .Contains("backlog", StringComparison.OrdinalIgnoreCase))
            {
                start = heading + 1;
            }
            searchFrom = lineEnd + 1;
        }
        var tail = start >= 0 ? notebook[start..] : notebook;
        return tail.Length > MaxTailChars ? tail[^MaxTailChars..] : tail;
    }

    /// <summary>Потолок инжекта хвоста записной книжки в системный промпт.</summary>
    private const int MaxTailChars = 2000;
}
