using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Mcp;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Mcp;

/// <summary>
/// MCP management tools (shelf "mcp"). Allows the agent to inspect and call
/// tools exposed by connected MCP servers.
/// </summary>

[Tool("mcp_status",
    "List all connected MCP servers and their available tools. " +
    "Shows server name, version, tool count, and transport. " +
    "Use for diagnostics: check if a server is connected, how many tools it exposes.",
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
            sb.AppendLine($"🔌 {name} (v{client.ServerInfo?.Version ?? "?"}) — {client.Tools.Count} tools, transport: {client.Name}");
        }
        return Task.FromResult(sb.ToString().TrimEnd());
    }
}
