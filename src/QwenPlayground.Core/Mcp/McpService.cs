using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Mcp;

namespace QwenPlayground.Core.Mcp;

/// <summary>
/// Static holder for the McpServerManager instance.
/// Initialized at app startup, disposed at shutdown.
/// </summary>
public static class McpService
{
    public static McpServerManager? Instance { get; private set; }

    /// <summary>Completes when initial MCP connection is done (success or failure).</summary>
    public static Task Ready { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Хук UI: перерегистрировать MCP-тулы в реестре (mcp_reload). Реестр владеет UI, и UI
    /// знает, на каком потоке его мутация легальна — Core не лезет в окна/диспетчер
    /// (паттерн AgentInteraction: маршрут интерактива регистрирует владелец UI).
    /// null — UI нет (Harness/тесты): перерегистрация не нужна, тулы не в реестре.
    /// </summary>
    public static Action? ReRegisterTools;

    public static async Task InitializeAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Ready = tcs.Task;
        try
        {
            StartupTrace.Log("MCP init: begin (background)");
            Instance = new McpServerManager();
            await Instance.ConnectAllAsync();
            StartupTrace.Log("MCP init: ConnectAllAsync done");

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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MCP] Init failed: {ex}");
        }
        finally
        {
            tcs.TrySetResult();
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
