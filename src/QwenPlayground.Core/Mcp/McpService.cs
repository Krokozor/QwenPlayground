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

            // Автопереподключение: восстанавливает серверы, не поднявшиеся на старте
            // (приложение-цель ещё не было запущено) и умершие во время сессии
            // (например, процесс relay).
            Instance.StartAutoReconnect();

            var clients = Instance.Clients;
            if (clients.Count > 0)
            {
                var tools = Instance.GetAllTools();
                DiagnosticsLog.Log($"MCP: connected {clients.Count} server(s), {tools.Count} tool(s): " +
                    string.Join(", ", tools.Select(t => t.NamespacedName)));
            }
            else
            {
                // Не исключение, но агент работает без MCP-тулов — видно только в
                // DiagnosticsLog (Debug.WriteLine в релизе молчит).
                DiagnosticsLog.Log("MCP: no servers connected — agent runs without MCP tools (auto-reconnect will keep trying)");
            }
        }
        catch (Exception ex)
        {
            // Раньше — только Debug.WriteLine: в релизе провал инициализации был
            // невидим, и агент молча работал «без части своих способностей».
            DiagnosticsLog.Log($"MCP: init failed: {ex}");
            QwenPlayground.Core.MetaInfo.AnnouncementBoard.Push("mcp-init",
                $"MCP-инициализация упала: {ex.Message} — MCP-тулы недоступны (auto-reconnect будет пробовать).");
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
            Instance.StopAutoReconnect();
            await Instance.DisconnectAllAsync();
            Instance = null;
        }
    }
}
