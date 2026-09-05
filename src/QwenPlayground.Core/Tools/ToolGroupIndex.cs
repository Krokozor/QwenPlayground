using QwenPlayground.Core.Chat;

namespace QwenPlayground.Core.Tools;

/// <summary>
/// Индекс полок (групп) инструментов в системном промпте. Динамический по состоянию:
/// markdown-таблица | group | status | description |, где description зависит от статуса —
/// · неактивная группа — «полный промпт» группы: что это, что она делает, когда активировать;
/// · активная группа — краткое упоминание со списком тулов группы (чтобы знать, что можно
///   выгрузить через deactivate_shelf).
/// Перед таблицей — инструкция: после оценки задачи, если она требует возможностей группы,
/// активировать её через activate_shelf; когда группа перестала нуждаться — deactivate_shelf.
/// Имена тулов активной группы — из рефлексии [Tool](name, desc, group) через ToolRegistry
/// (не хардкод). Блок меняется при активации/деактивации — но это та же смена промпта, что и
/// сами тулзы (KV-кеш и так пересобирается, см. [shelf-cache] в MainViewModel).
/// </summary>
public static class ToolGroupIndex
{
    /// <summary>
    /// «Полный промпт» неактивной группы (проза — не выводится из рефлексии): что это за
    /// группа, что она делает, когда её следует активировать. Без символов '|' (разделитель
    /// ячеек таблицы).
    /// </summary>
    private static readonly Dictionary<ToolGroup, string> InactiveDescriptions = new()
    {
        [ToolGroup.Browser] =
            "WebView2 browser — a real embedded browser you drive: navigate (URL, back, forward, reload), " +
            "click/type/hover/select/scroll via CSS selectors or coordinates (with trusted CDP mode for " +
            "anti-bot sites), press keys, wait for elements to appear/disappear, search page text (find where " +
            "to scroll), take screenshots (viewport, full page, frame series), extract text line-by-line, " +
            "evaluate arbitrary JS, read console and network logs. Every action returns a screenshot, so you " +
            "see what happened. Activate when the task involves a website: web automation, scraping, " +
            "inspecting a page, verifying UI in a real browser, debugging page behavior.",

        [ToolGroup.CSharp] =
            "Roslyn C# analysis over the workspace .NET solution — semantic, not just text search: find a " +
            "symbol's definition, all references and callers, compile diagnostics without a build, " +
            "file/namespace outline, class map (type hierarchy and relations). Activate when working with " +
            "C# code: understanding a codebase, tracing call chains, impact analysis before refactoring, " +
            "checking compile errors."
    };

    /// <summary>
    /// Блок индекса: инструкция + markdown-таблица | group | status | description |.
    /// Неактивные группы — «полный промпт» группы, активные — список тулов и подсказка
    /// деактивировать. Пусто, если нет не-core групп.
    /// </summary>
    public static string Render(IReadOnlyCollection<ToolGroup> active, ToolRegistry registry)
    {
        var groups = Enum.GetValues<ToolGroup>().Where(g => g != ToolGroup.Core).OrderBy(g => g).ToList();
        if (groups.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>
        {
            "# Tool groups (shelves)",
            string.Empty,
            "Tool groups are collections of tools added to your prompt on demand: the core set is always " +
            "available, the groups below are not. After evaluating the task, if it requires a group's " +
            "capability, activate that group with activate_shelf — its tools join your prompt starting next " +
            "turn. When you are done with a group, deactivate it with deactivate_shelf to free context.",
            string.Empty,
            "| group | status | description |",
            "|---|---|---|"
        };
        foreach (var g in groups)
        {
            var name = g.ToString().ToLowerInvariant();
            if (active.Contains(g))
            {
                var tools = registry.DefinitionsByGroup(g).Select(d => d.Name).OrderBy(n => n).ToList();
                lines.Add($"| {name} | active | its {tools.Count} tools are in your prompt: " +
                          $"{string.Join(", ", tools)}. Deactivate with deactivate_shelf when done. |");
            }
            else
            {
                var description = InactiveDescriptions.TryGetValue(g, out var d) && d.Length > 0
                    ? d
                    : "No description. Activate with activate_shelf if the task needs this group.";
                lines.Add($"| {name} | inactive | {description} |");
            }
        }
        return string.Join('\n', lines);
    }
}
