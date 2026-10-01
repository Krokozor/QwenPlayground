using System.Text.Json.Nodes;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Subagents;
using QwenPlayground.Core.Tools;
using QwenPlayground.Core.Tools.Builtins;
using Xunit;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// P5 (внешнее ревью 2026-10-01): субагент не может спавнить субагентов и пересобирать
/// приложение — даже если профиль (DeniedTools — данные в chat-profiles.json) разрешит.
/// Две линии: хард-блэклист скоупа в ToolRegistry (диспетчер) + глубина спавна
/// (SpawnDepth, AsyncLocal) в самом инструменте.
/// </summary>
public sealed class SubagentGuardsTests
{
    private static ToolContext Context(int? slotId, params string[] allowedTools) =>
        new(projectRoot: @"V:\QwenPlayground",
            slotId: slotId,
            allowedTools: allowedTools.Length > 0 ? (IReadOnlySet<string>)allowedTools.ToHashSet(StringComparer.Ordinal) : null);

    [Fact]
    public async Task ToolRegistry_SubagentScope_SpawnIsHardDenied_EvenIfProfileAllows()
    {
        var registry = new ToolRegistry();
        // Профиль «разрешил» spawn_subagent субагенту (AllowedTools содержит имя) —
        // хард-блэклист срабатывает раньше и независимо от профиля.
        var result = await registry.ExecuteAsync(
            "spawn_subagent", new JsonObject { ["task"] = "рекурсия" },
            Context(SlotAllocation.Subagent, "spawn_subagent"));

        Assert.Contains("hard-denied", result);
    }

    [Fact]
    public async Task ToolRegistry_SubagentScope_RebuildSelfIsHardDenied()
    {
        var registry = new ToolRegistry();
        var result = await registry.ExecuteAsync(
            "rebuild_self", new JsonObject(),
            Context(SlotAllocation.Subagent, "rebuild_self"));

        Assert.Contains("hard-denied", result);
    }

    [Fact]
    public async Task ToolRegistry_MainScope_SpawnPassesBlacklist()
    {
        var registry = new ToolRegistry();
        var result = await registry.ExecuteAsync(
            "spawn_subagent", new JsonObject { ["task"] = "x" },
            Context(SlotAllocation.Main, "spawn_subagent"));

        // Блэклист не сработал: дошли до самого инструмента (в тестах Main не инициализирован).
        Assert.DoesNotContain("hard-denied", result);
        Assert.Contains("недоступно", result);
    }

    [Fact]
    public async Task ToolRegistry_SubagentScope_OtherToolsNotDenied()
    {
        var registry = new ToolRegistry();
        var result = await registry.ExecuteAsync(
            "read_file", new JsonObject { ["path"] = "x" },
            Context(SlotAllocation.Subagent, "read_file"));

        // read_file вне блэклиста: дошли до инструмента (ошибка аргументов, не hard-denied).
        Assert.DoesNotContain("hard-denied", result);
    }

    [Fact]
    public async Task SpawnDepth_RunAsSubagentAsync_SetsAndRestores()
    {
        Assert.Equal(0, SpawnDepth.Current);
        var seen = await SpawnDepth.RunAsSubagentAsync(
            () => Task.FromResult(SpawnDepth.Current));
        Assert.Equal(1, seen);
        Assert.Equal(0, SpawnDepth.Current); // восстановлено
    }

    [Fact]
    public async Task SpawnDepth_Nested_IncreasesAndRestores()
    {
        var seen = await SpawnDepth.RunAsSubagentAsync(async () =>
            await SpawnDepth.RunAsSubagentAsync(() => Task.FromResult(SpawnDepth.Current)));
        Assert.Equal(2, seen);
        Assert.Equal(0, SpawnDepth.Current);
    }

    [Fact]
    public async Task SpawnSubagentTool_DepthGuard_RefusesInsideSubagentTurn()
    {
        var tool = new SpawnSubagentTool { Task = "рекурсия" };
        // Внутри «хода субагента» (глубина 1) тул отказывает ДО обращения к Main.
        var result = await SpawnDepth.RunAsSubagentAsync(
            () => tool.ExecuteAsync(Context(SlotAllocation.Subagent), CancellationToken.None));

        Assert.Contains("лимит глубины", result);
    }
}
