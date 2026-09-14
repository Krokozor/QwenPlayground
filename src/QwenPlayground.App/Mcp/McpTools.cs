using System.Text.Json.Nodes;
using QwenPlayground.App.Mcp;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Mcp;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Mcp;

/// <summary>
/// MCP management tools (shelf "mcp"). Allows the agent to inspect and call
/// tools exposed by connected MCP servers.
/// </summary>

[Tool("mcp_status",
    "List all connected MCP servers and their available tools. " +
    "Shows server name, version, and tool names with descriptions. " +
    "Use this to discover what MCP tools are available before calling mcp_call.",
    ToolGroup.Mcp)]
public sealed class McpStatusTool : AgentTool
{
    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var manager = McpService.Instance;
        if (manager is null)
            return Task.FromResult("MCP service not initialized.");

        var clients = manager.Clients;
        if (clients.Count == 0)
            return Task.FromResult("No MCP servers connected.");

        var sb = new System.Text.StringBuilder();
        foreach (var kvp in clients)
        {
            var name = kvp.Key;
            var client = kvp.Value;
            sb.AppendLine($"🔌 {name} (v{client.ServerInfo?.Version ?? "?"}) — {client.Tools.Count} tools:");
            foreach (var tool in client.Tools)
            {
                sb.AppendLine($"  • mcp_{name}_{tool.Name}: {tool.Description}");
            }
        }
        return Task.FromResult(sb.ToString().TrimEnd());
    }
}

[Tool("mcp_call",
    "Call a tool on a connected MCP server. The tool name must be the namespaced form: " +
    "mcp_{server}_{tool} (e.g. mcp_blender_render_scene). " +
    "Use mcp_status first to discover available tools and their parameters. " +
    "The 'arguments' parameter is a JSON object with the tool's input parameters.",
    ToolGroup.Mcp)]
public sealed class McpCallTool : AgentTool
{
    [ToolParameter("Namespaced tool name: mcp_{server}_{tool} (e.g. mcp_test_echo)", Required = true)]
    public string ToolName { get; set; } = string.Empty;

    [ToolParameter("JSON object with the tool's input parameters (e.g. {\"text\": \"hello\"})", Required = true)]
    public string Arguments { get; set; } = "{}";

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var manager = McpService.Instance;
        if (manager is null)
            return "MCP service not initialized.";

        JsonObject args;
        try
        {
            args = JsonNode.Parse(Arguments) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            return $"Error: invalid JSON in arguments: {ex.Message}";
        }

        try
        {
            var result = await manager.CallToolAsync(ToolName, args, cancellationToken);
            return result;
        }
        catch (McpException ex)
        {
            return $"MCP error [{ex.Code}]: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
