using QwenPlayground.Core.Mcp;

namespace QwenPlayground.App.Mcp;

/// <summary>
/// Static holder for the McpServerManager instance.
/// Initialized at app startup, disposed at shutdown.
/// </summary>
public static class McpService
{
    public static McpServerManager? Instance { get; private set; }

    public static async Task InitializeAsync()
    {
        Instance = new McpServerManager();
        await Instance.ConnectAllAsync();

        var clients = Instance.Clients;
        if (clients.Count > 0)
        {
            var tools = Instance.GetAllTools();
            System.Diagnostics.Debug.WriteLine(
                $"[MCP] Connected {clients.Count} server(s), {tools.Count} tool(s): " +
                string.Join(", ", tools.Select(t => t.NamespacedName)));
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[MCP] No servers connected.");
        }
    }

    public static async Task ShutdownAsync()
    {
        if (Instance is not null)
        {
            await Instance.DisconnectAllAsync();
            Instance = null;
        }
    }
}
