using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Mcp;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Mcp;

/// <summary>
/// Генерирует ToolEntry для каждого тула подключённых MCP-серверов.
/// Имя: {server}_{tool} (без префикса "mcp_"). Group: ToolGroup.Mcp.
/// При коллизии с built-in тулом — warn + skip (не регистрируем).
/// </summary>
public static class McpToolRegistrar
{
    /// <summary>
    /// Зарегистрировать все MCP-тулы в реестре. Возвращает список предупреждений
    /// (коллизии) и число зарегистрированных тулов.
    /// </summary>
    public static (int Registered, List<string> Warnings) RegisterAll(ToolRegistry registry, McpServerManager manager)
    {
        var warnings = new List<string>();
        var registered = 0;

        // Сначала снимаем старые MCP-тулы (переподключение)
        foreach (var (serverName, _) in manager.Clients)
        {
            registry.UnregisterByPrefix(serverName + "_");
        }

        foreach (var (serverName, client) in manager.Clients)
        {
            foreach (var tool in client.Tools)
            {
                var toolName = $"{serverName}_{tool.Name}";

                var definition = new ToolDefinition
                {
                    Name = toolName,
                    Description = tool.Description,
                    Parameters = tool.InputSchema as JsonObject ?? DefaultSchema(),
                    Group = ToolGroup.Mcp
                };

                var entry = new ToolEntry
                {
                    Definition = definition,
                    Execute = async (arguments, context, ct) =>
                    {
                        var mcpManager = McpService.Instance;
                        if (mcpManager is null)
                            return new ToolExecutionResult("Error: MCP service not initialized.", null);
                        try
                        {
                            var result = await mcpManager.CallToolAsync(toolName, arguments, ct);
                            return new ToolExecutionResult(result, null);
                        }
                        catch (McpException ex)
                        {
                            return new ToolExecutionResult($"MCP error [{ex.Code}]: {ex.Message}", null);
                        }
                        catch (Exception ex)
                        {
                            return new ToolExecutionResult($"Error: {ex.Message}", null);
                        }
                    }
                };

                if (registry.TryRegister(entry))
                {
                    registered++;
                }
                else
                {
                    warnings.Add($"MCP tool '{toolName}' skipped: name collides with built-in tool. " +
                                  $"Rename the MCP server (currently '{serverName}') to avoid the collision.");
                }
            }
        }

        return (registered, warnings);
    }

    /// <summary>Удалить все MCP-тулы из реестра.</summary>
    public static int UnregisterAll(ToolRegistry registry, McpServerManager manager)
    {
        var removed = 0;
        foreach (var (serverName, _) in manager.Clients)
        {
            removed += registry.UnregisterByPrefix(serverName + "_");
        }
        return removed;
    }

    private static JsonObject DefaultSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject()
    };
}
