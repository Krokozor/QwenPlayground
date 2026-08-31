using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Шов плагинных инструментов: Register() — точка входа для MCP-клиента и плагинов.
/// Динамический инструмент не имеет [Tool]-класса — только определение + делегат.
/// </summary>
public sealed class ToolRegistryDynamicTests
{
    private readonly ToolRegistry _registry = new();
    private readonly ToolContext _context = new(".");

    [Fact]
    public async Task Register_DynamicTool_IsAdvertisedAndExecutable()
    {
        var executed = false;
        _registry.Register(new ToolEntry
        {
            Definition = new ToolDefinition
            {
                Name = "mcp_echo",
                Description = "эхо из MCP",
                Parameters = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["text"] = new JsonObject { ["type"] = "string", ["description"] = "что вернуть" }
                    }
                }
            },
            Execute = (args, context, cancellationToken) =>
            {
                executed = true;
                return Task.FromResult(new ToolExecutionResult("echo:" + args["text"], null));
            }
        });

        Assert.Contains(_registry.Definitions, d => d.Name == "mcp_echo");

        var result = await _registry.ExecuteAsync(
            "mcp_echo", new JsonObject { ["text"] = "привет" }, _context);

        Assert.True(executed);
        Assert.Equal("echo:привет", result);
    }

    [Fact]
    public void Register_DuplicateName_Throws()
    {
        _registry.Register(new ToolEntry
        {
            Definition = new ToolDefinition { Name = "dup_tool", Description = "1", Parameters = new JsonObject() },
            Execute = (args, context, cancellationToken) => Task.FromResult(new ToolExecutionResult("", null))
        });

        Assert.Throws<InvalidOperationException>(() => _registry.Register(new ToolEntry
        {
            Definition = new ToolDefinition { Name = "dup_tool", Description = "2", Parameters = new JsonObject() },
            Execute = (args, context, cancellationToken) => Task.FromResult(new ToolExecutionResult("", null))
        }));
    }

    [Fact]
    public void Definitions_RemainSortedAfterRegistration()
    {
        // Порядок в промпте стабилен и не «дышит» после динамической регистрации.
        _registry.Register(new ToolEntry
        {
            Definition = new ToolDefinition { Name = "zzz_last", Description = "", Parameters = new JsonObject() },
            Execute = (args, context, cancellationToken) => Task.FromResult(new ToolExecutionResult("", null))
        });
        _registry.Register(new ToolEntry
        {
            Definition = new ToolDefinition { Name = "aaa_first", Description = "", Parameters = new JsonObject() },
            Execute = (args, context, cancellationToken) => Task.FromResult(new ToolExecutionResult("", null))
        });

        var names = _registry.Definitions.Select(d => d.Name).ToList();
        var sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(sorted, names);
    }

    [Fact]
    public async Task DynamicTool_Exception_BecomesErrorText()
    {
        _registry.Register(new ToolEntry
        {
            Definition = new ToolDefinition { Name = "boom", Description = "", Parameters = new JsonObject() },
            Execute = (args, context, cancellationToken) => throw new InvalidOperationException("бах")
        });

        var result = await _registry.ExecuteAsync("boom", new JsonObject(), _context);

        Assert.Contains("Error:", result);
    }
}
