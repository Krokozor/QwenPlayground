using System.Text.Json.Nodes;
using QwenPlayground.Core.Main;
using QwenPlayground.Core.Subagents;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Синхронный спавн субагента (решение 2026-09-22): отдельный агент с собственным
/// контекстом и инструментами (профиль «subagent», слот 3) выполняет задачу и возвращает
/// отчёт как ответ этого тула. Пока ходит субагент, main-агент ждёт: его KV-кеш
/// сохраняется в файл (sessions/&lt;id&gt;-kv.bin) и восстанавливается после (SubagentSpawner).
///
/// Доступен main-агенту и побочным окнам; субагентам — НЕТ (DeniedTools в их профиле:
/// без рекурсии). Идентичность/промпт субагента правится в редакторе профилей (ключ «subagent»).
/// </summary>
[Tool("spawn_subagent",
    "Launch a subagent: a separate agent with its own context and tools that executes a task " +
    "synchronously and returns a report as this tool's answer. While the subagent runs, you wait " +
    "(your context is saved to a file and restored afterwards). Use it for tasks that should not " +
    "be pulled into your own context: large research, file work, reports.\n\n" +
    "When NOT to use it:\n" +
    "- A single file read or a search across 2-3 files — do it yourself with read_file/glob/grep (faster).\n" +
    "- A simple shell command — run it yourself.\n" +
    "- A task that needs YOUR context: the subagent cannot see it — you would have to re-describe everything in task.\n\n" +
    "Rules:\n" +
    "- task — a complete formulation: goal, context, constraints, what counts as the result.\n" +
    "- State explicitly: write code/files or only research (read, search, web).\n" +
    "- If possible — how the subagent can verify its own work (tests, build, commands).\n" +
    "- Do not duplicate the delegated work yourself. Trust the subagent's report; it is not visible to the owner — retell it yourself.\n\n" +
    "The subagent window stays open: the owner can observe the work and continue the conversation with it.")]
public sealed class SpawnSubagentTool : AgentTool
{
    [ToolParameter("Complete task formulation for the subagent: goal, context, constraints, what counts as the result. The subagent cannot see your context — describe everything it needs.", Required = true)]
    public string Task { get; set; } = string.Empty;

    [ToolParameter("Short task name (2-6 words) — for the subagent window title.", Required = false)]
    public string? Title { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        // Глубина спавна (P5, defense in depth): субагент не спавнит субагентов.
        // Основной отказ — хард-блэклист скоупа в ToolRegistry; это вторая линия.
        if (SpawnDepth.Current >= 1)
        {
            return "spawn_subagent: subagents do not launch subagents (depth limit 1). " +
                   "Return your report to the calling agent — it will decide what to do next.";
        }

        var task = Task.Trim();
        if (task.Length == 0)
        {
            return "spawn_subagent: task field is empty — describe the task for the subagent.";
        }

        // Полное имя: «Main» здесь неоднозначно (класс и одноимённое пространство имён).
        var main = QwenPlayground.Core.Main.Main.Instance;
        if (main is null)
        {
            return "spawn_subagent: unavailable (composition root not initialized).";
        }

        // Слот вызывающего: main → 0, побочное окно → 2 (ToolContext.SlotId из пиннинга хода).
        var outcome = await main.Subagents.SpawnAsync(task, Title?.Trim(), context.SlotId, cancellationToken);
        var note = outcome.KvNote is null ? string.Empty : $"\n\n[KV-cache note: {outcome.KvNote}]";
        var anchored = outcome.KvAnchored ? "The main agent's context was saved to a file and restored." : string.Empty;
        return $"The subagent finished the task. {anchored}\n\n— Subagent report —\n{outcome.Report}{note}";
    }
}
