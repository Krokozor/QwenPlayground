using System.Reflection;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Tools;

/// <summary>
/// Управление полками (группами) инструментов: активация докидывает тулзы группы в системный
/// промпт (следующий ход), деактивация — убирает. Состояние — per-session (sessions/&lt;id&gt;/shelves.json).
/// Обе операции меняют системный промпт → инвалидируют KV-кеш; см. ToolGroup.
/// Ответ на активацию/деактивацию содержит список тулов группы (из рефлексии [Tool]) —
/// иначе агент не узнает, что именно изменилось в его промпте.
/// </summary>
[Tool("activate_shelf", "Activate a tool group (shelf): adds its tools to your prompt starting next turn. " +
    "Available groups: browser (WebView2 web automation), csharp (Roslyn code analysis). " +
    "Activate when you need the group's capability. Deactivate with deactivate_shelf when done.")]
public sealed class ActivateShelfTool : AgentTool
{
    [ToolParameter("Group to activate: 'browser' or 'csharp'", Required = true)]
    public string Group { get; set; } = string.Empty;

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (!TryParseGroup(Group, out var group))
        {
            return Task.FromResult($"Error: unknown group '{Group}'. Available: browser, csharp.");
        }
        if (context.SessionDir is null)
        {
            return Task.FromResult("Error: no session in this context — cannot activate a shelf.");
        }
        var state = new ShelfState(context.SessionDir);
        var active = state.Load();
        var wasPending = state.LoadPending().Contains(group);
        if (active.Contains(group) && !wasPending)
        {
            return Task.FromResult($"Group '{Group}' is already active. Its tools are in your prompt: " +
                                    $"{GroupToolNames(group)}.");
        }
        // Пере-активация отменяет отложенную деактивацию: группа понадобилась снова →
        // решение на выключение откатывается, rebuild не происходит (группа и так в промпте).
        state.UnmarkPending(group);
        if (!active.Contains(group))
        {
            active.Add(group);
            state.Save(active);
        }
        if (wasPending)
        {
            return Task.FromResult($"Group '{Group}' re-activated — the pending deactivation is canceled. " +
                                    $"Its tools stay in your prompt: {GroupToolNames(group)}. No prompt change.");
        }
        return Task.FromResult($"Group '{Group}' activated — its tools join your prompt next turn: " +
                                $"{GroupToolNames(group)}. System prompt changed (KV-cache rebuild).");
    }

    internal static bool TryParseGroup(string name, out ToolGroup group) =>
        Enum.TryParse<ToolGroup>(name.Trim(), ignoreCase: true, out group) && group != ToolGroup.Core;

    /// <summary>
    /// Имена тулов группы через рефлексию [Tool] (тот же источник, что и ToolRegistry, но без
    /// сборки реестра — в ответ инструмента нужны только имена). Скан дешёвый и редкий:
    /// вызывается только при активации/деактивации полки.
    /// </summary>
    internal static string GroupToolNames(ToolGroup group)
    {
        var names = new List<string>();
        foreach (var assembly in new[] { typeof(AgentTool).Assembly, typeof(ActivateShelfTool).Assembly })
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(AgentTool).IsAssignableFrom(type))
                {
                    continue;
                }
                if (type.GetCustomAttribute<ToolAttribute>() is { Group: var g, Name: var name } attribute
                    && g == group)
                {
                    names.Add(name);
                }
            }
        }
        return string.Join(", ", names.OrderBy(n => n, StringComparer.Ordinal));
    }
}

[Tool("deactivate_shelf", "Schedule a tool group (shelf) for deactivation: its tools leave your prompt at the " +
    "next natural system-prompt change (compaction/session switch), not immediately — this avoids an extra " +
    "KV-cache rebuild. Until then the group's tools remain available. Re-activating the group cancels the " +
    "scheduled deactivation. Use when you no longer need the group's capability. Groups: browser, csharp.")]
public sealed class DeactivateShelfTool : AgentTool
{
    [ToolParameter("Group to deactivate: 'browser' or 'csharp'", Required = true)]
    public string Group { get; set; } = string.Empty;

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (!ActivateShelfTool.TryParseGroup(Group, out var group))
        {
            return Task.FromResult($"Error: unknown group '{Group}'. Available: browser, csharp.");
        }
        if (context.SessionDir is null)
        {
            return Task.FromResult("Error: no session in this context — cannot deactivate a shelf.");
        }
        var state = new ShelfState(context.SessionDir);
        var active = state.Load();
        if (!active.Contains(group))
        {
            return Task.FromResult($"Group '{Group}' is not active.");
        }
        // Staged-деактивация: не снимаем группу сразу (это создало бы собственный rebuild
        // системного промпта). Помечаем к снятию — группа уйдёт при ближайшей ЕСТЕСТВЕННОЙ
        // смене промпта (компакция/смена сессии/слои), батчингом с неизбежным rebuild'ом.
        // Пока группа в промпте — тулзы группы по-прежнему доступны.
        state.MarkPending(group);
        return Task.FromResult($"Group '{Group}' scheduled for deactivation — its tools leave your prompt " +
                                $"at the next natural system-prompt change (compaction/session switch). " +
                                $"Until then the tools remain available: " +
                                $"{ActivateShelfTool.GroupToolNames(group)}. No prompt change now (KV-cache preserved).");
    }
}
