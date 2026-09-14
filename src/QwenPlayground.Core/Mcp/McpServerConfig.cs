namespace QwenPlayground.Core.Mcp;

/// <summary>
/// Configuration for a single MCP server connection.
/// Transport: "stdio" (launch a local process) or "http" (connect to an HTTP endpoint).
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>Unique name for this server (used in tool names: mcp_{name}_{tool}).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether this server is enabled and should be connected at startup.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>"stdio" or "http".</summary>
    public string Transport { get; set; } = "stdio";

    // ── stdio transport ──
    /// <summary>Executable to launch (stdio only), e.g. "blender-mcp" or "C:\tools\mcp-server.exe".</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Command-line arguments (stdio only).</summary>
    public List<string> Args { get; set; } = new();

    /// <summary>Environment variables for the process (stdio only).</summary>
    public Dictionary<string, string> Env { get; set; } = new();

    // ── http transport ──
    /// <summary>HTTP endpoint URL (http only), e.g. "http://localhost:9876".</summary>
    public string Url { get; set; } = string.Empty;
}
