using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Mcp;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Mcp;

/// <summary>
/// Manages the lifecycle of all configured MCP servers.
/// Connects to enabled servers at startup, exposes their tools,
/// and handles reconnection.
/// </summary>
public sealed class McpServerManager
{
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>All connected clients, keyed by server name.</summary>
    public IReadOnlyDictionary<string, McpClient> Clients
    {
        get { lock (_lock) return new Dictionary<string, McpClient>(_clients); }
    }

    /// <summary>
    /// Connect to all enabled MCP servers from settings.
    /// Failures are logged but don't prevent other servers from connecting.
    /// </summary>
    public async Task ConnectAllAsync(CancellationToken ct = default)
    {
        var settings = AppSettings.Get();
        foreach (var config in settings.McpServers.Where(s => s.Enabled))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            StartupTrace.Log($"MCP connect: '{config.Name}' begin ({config.Transport})");
            try
            {
                await ConnectAsync(config, ct);
                StartupTrace.Log($"MCP connect: '{config.Name}' done ({sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                StartupTrace.Log($"MCP connect: '{config.Name}' FAILED ({sw.ElapsedMilliseconds}ms): {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MCP] Failed to connect to '{config.Name}': {ex}");
                // Сбой коннекта — важное событие: на доску анонсов (видно в state-блоке).
                QwenPlayground.Core.MetaInfo.AnnouncementBoard.Push("mcp:" + config.Name,
                    "не подключился на старте: " + ex.Message);
                // Also write to a log file for debugging. Сбой записи в лог не должен
                // ломать цикл коннекта — сама ошибка уже анонсирована выше.
                try
                {
                    var logPath = Path.Combine(QwenPlayground.Core.SelfBuild.SelfBuildPaths.WorkspaceRoot, "logs", "mcp_errors.log");
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    System.IO.File.AppendAllText(logPath,
                        $"[{DateTime.Now:HH:mm:ss}] {config.Name}: {ex}\n");
                }
                catch { }
            }
        }
    }

    /// <summary>Connect to a single MCP server by config.</summary>
    public async Task<McpClient> ConnectAsync(McpServerConfig config, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(config.Name, out var existing))
            {
                // Already connected — return existing
                return existing;
            }
        }

        var client = new McpClient(config);
        await client.InitializeAsync(ct);

        lock (_lock)
        {
            _clients[config.Name] = client;
        }

        return client;
    }

    /// <summary>Disconnect a server by name.</summary>
    public async Task DisconnectAsync(string name)
    {
        McpClient? client;
        lock (_lock)
        {
            if (!_clients.TryGetValue(name, out client)) return;
            _clients.Remove(name);
        }
        if (client is not null)
            await client.DisposeAsync();
    }

    /// <summary>Disconnect all servers.</summary>
    public async Task DisconnectAllAsync()
    {
        List<McpClient> toDispose;
        lock (_lock)
        {
            toDispose = _clients.Values.ToList();
            _clients.Clear();
        }
        foreach (var client in toDispose)
            await client.DisposeAsync();
    }

    /// <summary>
    /// Get all tools from all connected servers, with namespaced names: {server}_{tool}.
    /// </summary>
    public List<(string NamespacedName, McpToolInfo Tool, string ServerName)> GetAllTools()
    {
        var result = new List<(string, McpToolInfo, string)>();
        lock (_lock)
        {
            foreach (var (serverName, client) in _clients)
            {
                foreach (var tool in client.Tools)
                {
                    var namespaced = $"{serverName}_{tool.Name}";
                    result.Add((namespaced, tool, serverName));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Call a tool by its name ({server}_{tool}, e.g. "blender_execute_code").
    /// </summary>
    public async Task<string> CallToolAsync(string toolName, System.Text.Json.Nodes.JsonObject arguments, CancellationToken ct = default)
    {
        // Parse: {server}_{tool} — find the server prefix
        var underscoreIdx = toolName.IndexOf('_');
        if (underscoreIdx <= 0 || underscoreIdx == toolName.Length - 1)
            throw new ArgumentException($"Invalid MCP tool name: {toolName}. Expected format: {{server}}_{{tool}}");

        var serverName = toolName[..underscoreIdx];
        var mcpToolName = toolName[(underscoreIdx + 1)..];

        McpClient? client;
        lock (_lock)
        {
            if (!_clients.TryGetValue(serverName, out client))
                throw new McpException(-1, $"MCP server '{serverName}' is not connected");
        }

        return await client!.CallToolAsync(mcpToolName, arguments, ct);
    }
}
