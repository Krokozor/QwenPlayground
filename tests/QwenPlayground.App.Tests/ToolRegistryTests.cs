using System.IO;
using System.Text.Json.Nodes;
using QwenPlayground.App.Tools;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Tests;

/// <summary>
/// Инварианты реестра инструментов. Ловят ровно тот класс поломки, из-за которого
/// pigeon-тулы оказались «недоступны»: класс не попал в сборку (edit сорвался) или
/// имя столкнулось с уже зарегистрированным (Register бросает на дубликате).
/// Отдельно фиксируем порядок проверок: неизвестное имя должно отвечать «unknown tool»,
/// а не «зарегистрирован, но профиль не рекламирует».
/// </summary>
public sealed class ToolRegistryTests
{
    private static readonly string[] PigeonFileTools =
    [
        "pigeon_ls",
        "pigeon_read_file",
        "pigeon_download",
        "pigeon_write",
        "pigeon_edit",
        "pigeon_upload",
        "pigeon_delete",
    ];

    /// <summary>Те же сборки, что передаёт композиционный корень (MainViewModel).</summary>
    private static ToolRegistry NewRegistry() =>
        new(typeof(AgentTool).Assembly, typeof(PigeonConnectTool).Assembly);

    [Fact]
    public void Registry_RealAssemblies_RegisterWithoutDuplicateNames()
    {
        var registry = NewRegistry();

        var names = registry.Definitions.Select(d => d.Name).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Registry_AdvertisesPigeonFilePoolToolsOnPigeonShelf()
    {
        var definitions = NewRegistry().Definitions;

        foreach (var name in PigeonFileTools)
        {
            var definition = Assert.Single(definitions, d => d.Name == name);
            Assert.Equal(ToolGroup.Pigeon, definition.Group);
        }
        // Чатовый ридер остаётся отдельным инструментом: коллизия имён валит регистрацию.
        Assert.Contains(definitions, d => d.Name == "pigeon_read");
    }

    [Fact]
    public async Task Execute_UnknownTool_SaysUnknownNotProfileDenial()
    {
        var registry = new ToolRegistry(typeof(AgentTool).Assembly);
        var context = new ToolContext(
            Path.GetTempPath(),
            allowedTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "read_file" });

        var result = await registry.ExecuteDetailedAsync("pigeon_write", new JsonObject(), context);

        Assert.Contains("unknown tool", result.Text);
        Assert.DoesNotContain("registered in the process", result.Text);
    }

    [Fact]
    public async Task Execute_RegisteredToolOutsideTurnSet_ReportsProfileDenial()
    {
        var registry = new ToolRegistry(typeof(AgentTool).Assembly);
        var context = new ToolContext(
            Path.GetTempPath(),
            allowedTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var result = await registry.ExecuteDetailedAsync("read_file", new JsonObject(), context);

        Assert.Contains("not available in this scope", result.Text);
    }
}
