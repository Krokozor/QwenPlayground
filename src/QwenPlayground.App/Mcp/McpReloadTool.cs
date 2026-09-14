using QwenPlayground.Core.Mcp;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Tools;
using QwenPlayground.Core.Chat;

namespace QwenPlayground.App.Mcp;

[Tool("mcp_reload",
    "Re-read MCP server settings and (re)connect all enabled servers. " +
    "Use after adding/editing MCP servers in settings.json. " +
    "Already-connected servers are kept; new ones are connected, removed ones are disconnected.",
    ToolGroup.Mcp)]
public sealed class McpReloadTool : AgentTool
{
    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var manager = McpService.Instance;
        if (manager is null)
            return "MCP service not initialized.";

        var settings = AppSettings.Get();
        var enabled = settings.McpServers.Where(s => s.Enabled).ToList();
        var enabledNames = new HashSet<string>(enabled.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

        // Disconnect ALL existing servers (config may have changed)
        var current = manager.Clients;
        foreach (var name in current.Keys.ToList())
        {
            await manager.DisconnectAsync(name);
        }

        // Connect all enabled servers fresh
        var connected = new List<string>();
        var failed = new List<string>();
        foreach (var config in enabled)
        {
            try
            {
                await manager.ConnectAsync(config, cancellationToken);
                connected.Add(config.Name);
            }
            catch (Exception ex)
            {
                failed.Add($"{config.Name}: {ex.Message}");
            }
        }

        var result = new System.Text.StringBuilder();
        var allClients = manager.Clients;
        result.AppendLine($"MCP servers: {allClients.Count} connected.");
        foreach (var kvp in allClients)
        {
            result.AppendLine($"  🔌 {kvp.Key} — {kvp.Value.Tools.Count} tools");
        }
        if (connected.Count > 0) result.AppendLine($"Newly connected: {string.Join(", ", connected)}");
        if (failed.Count > 0) result.AppendLine($"Failed: {string.Join("; ", failed)}");
        return result.ToString().TrimEnd();
    }
}
