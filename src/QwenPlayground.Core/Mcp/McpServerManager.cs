using System.Text.Json.Nodes;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Mcp;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Mcp;

/// <summary>
/// Manages the lifecycle of all configured MCP servers.
/// Connects to enabled servers at startup, exposes their tools,
/// and handles reconnection.
///
/// Автопереподключение (A3, 2026-10-04, docs/stability-research.md):
/// - фоновый цикл каждые <see cref="AutoReconnectInterval"/> восстанавливает
///   включённые, но отключившиеся/умершие серверы (relay умер; приложение-цель
///   не было запущено на момент старта клиента и т.п.);
/// - lazy-reconnect в <see cref="CallToolAsync"/>: вызов тула мёртвого сервера
///   сначала пробует его восстановить;
/// - после успешного переподключения — ReRegisterTools (набор тулов мог измениться).
/// </summary>
public sealed class McpServerManager
{
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<McpClient>> _connecting = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private CancellationTokenSource? _autoReconnectCts;

    /// <summary>Период фонового автопереподключения.</summary>
    private static readonly TimeSpan AutoReconnectInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Таймаут tool-вызова (B3): защита от зависшего сервера. Значение покрывает
    /// худший случай relay (120 с ожидания bridge + 600 с вызова = 720 с) — канал
    /// последовательный, поэтому зависший вызов блокирует и следующие; таймаут
    /// ограничивает это время.
    /// </summary>
    private static readonly TimeSpan ToolCallTimeout = TimeSpan.FromSeconds(720);

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
                // Автопереподключение (если запущено) продолжит попытки в фоне.
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

    /// <summary>
    /// Connect to a single MCP server by config.
    /// Живой клиент уже есть — вернуть его. Есть, но мёртв (процесс завершился) —
    /// dispose и переподключить. Параллельные вызовы для одного сервера ждут
    /// одного коннекта (нет гонок «два процесса relay»).
    /// </summary>
    public async Task<McpClient> ConnectAsync(McpServerConfig config, CancellationToken ct = default)
    {
        // await СТРОГО вне lock: блокировка живёт только в момент выборки/регистрации.
        Task<McpClient> shared;
        bool isCreator;
        lock (_lock)
        {
            if (_clients.TryGetValue(config.Name, out var existing) && existing.IsTransportAlive)
                return existing;
            if (_connecting.TryGetValue(config.Name, out var inFlight))
            {
                shared = inFlight;
                isCreator = false;
            }
            else
            {
                shared = ConnectCoreAsync(config, ct);
                _connecting[config.Name] = shared;
                isCreator = true;
            }
        }
        try
        {
            return await shared;
        }
        finally
        {
            // Убираем за собой только создатель — чужой in-flight коннект не трогаем.
            if (isCreator)
                lock (_lock) _connecting.Remove(config.Name);
        }
    }

    private async Task<McpClient> ConnectCoreAsync(McpServerConfig config, CancellationToken ct)
    {
        // Мёртвый существующий клиент (процесс завершился во время сессии) — освободить.
        McpClient? dead = null;
        lock (_lock)
        {
            if (_clients.TryGetValue(config.Name, out var existing) && !existing.IsTransportAlive)
            {
                dead = existing;
                _clients.Remove(config.Name);
            }
        }
        if (dead is not null)
        {
            try { await dead.DisposeAsync(); } catch { /* best effort: процесс и так мёртв */ }
        }

        var client = new McpClient(config);
        await client.InitializeAsync(ct);

        lock (_lock)
        {
            _clients[config.Name] = client;
        }

        return client;
    }

    /// <summary>
    /// Start the background auto-reconnect loop (idempotent).
    /// Каждые <see cref="AutoReconnectInterval"/> восстанавливает включённые, но
    /// отключившиеся/мёртвые серверы.
    /// </summary>
    public void StartAutoReconnect()
    {
        lock (_lock)
        {
            if (_autoReconnectCts is not null) return;
            _autoReconnectCts = new CancellationTokenSource();
        }
        var token = _autoReconnectCts.Token;
        _ = Task.Run(() => AutoReconnectLoopAsync(token));
    }

    /// <summary>Stop the auto-reconnect loop (idempotent).</summary>
    public void StopAutoReconnect()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _autoReconnectCts;
            _autoReconnectCts = null;
        }
        cts?.Cancel();
    }

    private async Task AutoReconnectLoopAsync(CancellationToken token)
    {
        // Не спамить доску: failure анонсируем один раз (пока не подключится),
        // success — тоже один раз (если был failure).
        var announcedFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(AutoReconnectInterval, token); }
            catch (OperationCanceledException) { break; }

            List<McpServerConfig> enabled;
            try { enabled = AppSettings.Get().McpServers.Where(s => s.Enabled).ToList(); }
            catch { continue; } // настройки временно недоступны — цикл жив

            foreach (var config in enabled)
            {
                if (token.IsCancellationRequested) break;
                bool need;
                lock (_lock)
                {
                    need = !_clients.TryGetValue(config.Name, out var c) || !c.IsTransportAlive;
                }
                if (!need) continue;

                try
                {
                    await ConnectAsync(config, token);
                    if (announcedFailed.Remove(config.Name))
                        AnnouncementBoard.Push("mcp:" + config.Name, "сервер переподключён автоматически");
                    // Набор тулов мог измениться (relay перешёл handshake с bridge) —
                    // перерегистрировать. Хук знает, на каком потоке мутация реестра легальна.
                    try { McpService.ReRegisterTools?.Invoke(); }
                    catch { /* best effort: на следующем mcp_reload перерегистрируются */ }
                }
                catch (Exception ex)
                {
                    if (announcedFailed.Add(config.Name))
                        AnnouncementBoard.Push("mcp:" + config.Name,
                            "автопереподключение: " + ex.Message + " (повтор через ~30 с)");
                }
            }
        }
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
    /// Сервер не подключён или его процесс умер — сначала lazy-reconnect
    /// (приложение-цель могло запуститься после старта клиента).
    /// </summary>
    public async Task<string> CallToolAsync(string toolName, JsonObject arguments, CancellationToken ct = default)
    {
        // Parse: {server}_{tool} — find the server prefix
        var underscoreIdx = toolName.IndexOf('_');
        if (underscoreIdx <= 0 || underscoreIdx == toolName.Length - 1)
            throw new ArgumentException($"Invalid MCP tool name: {toolName}. Expected format: {{server}}_{{tool}}");

        var serverName = toolName[..underscoreIdx];
        var mcpToolName = toolName[(underscoreIdx + 1)..];

        var client = await GetOrReconnectAsync(serverName, ct);

        // Таймаут (B3): канал последовательный — зависший вызов блокирует сервер,
        // таймаут ограничивает время блокировки и даёт явную ошибку вместо «висящего» хода.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ToolCallTimeout);
        try
        {
            return await client.CallToolAsync(mcpToolName, arguments, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new McpException(-1,
                $"MCP tool call '{toolName}' timed out after {ToolCallTimeout.TotalSeconds:F0}s (server may be hung)");
        }
    }

    /// <summary>Живой клиент по имени сервера; иначе — попытка (пере)подключения.</summary>
    private async Task<McpClient> GetOrReconnectAsync(string serverName, CancellationToken ct)
    {
        McpClient? client;
        lock (_lock)
        {
            client = _clients.TryGetValue(serverName, out var c) ? c : null;
        }
        if (client is not null && client.IsTransportAlive)
            return client;

        var config = AppSettings.Get().McpServers
            .FirstOrDefault(s => s.Enabled && string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
        if (config is null)
            throw new McpException(-1,
                $"MCP server '{serverName}' is not connected and has no enabled config");

        try
        {
            return await ConnectAsync(config, ct);
        }
        catch (McpException ex)
        {
            throw new McpException(ex.Code, $"MCP server '{serverName}' reconnect failed: {ex.Message}");
        }
    }
}
