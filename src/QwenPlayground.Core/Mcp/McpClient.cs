using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Crash;

namespace QwenPlayground.Core.Mcp;

/// <summary>
/// MCP (Model Context Protocol) client. Supports stdio and HTTP transports.
/// Implements the JSON-RPC 2.0 protocol as specified by MCP.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly McpServerConfig _config;
    private readonly HttpClient _http;
    private Process? _process;
    private int _nextId = 1;
    private bool _initialized;
    private string? _sessionId;

    /// <summary>Tools discovered from this server.</summary>
    public List<McpToolInfo> Tools { get; } = new();

    /// <summary>Server info from initialize response.</summary>
    public McpServerInfo? ServerInfo { get; private set; }

    public string Name => _config.Name;
    public bool IsConnected { get; private set; }

    public McpClient(McpServerConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Connect to the MCP server: launch process (stdio) or set up HTTP,
    /// then perform the initialize handshake and discover tools.
    /// Times out after 15 seconds if the server doesn't respond.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        if (_config.Transport == "stdio")
            await StartStdioAsync(token.Token);
        else
            IsConnected = true;

        // MCP initialize handshake
        JsonObject? initResult;
        try
        {
            initResult = await SendRequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "QwenPlayground",
                    ["version"] = "1.0"
                }
            }, token.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Timeout — capture stderr for diagnostics
            var stderr = _process?.StandardError.ReadToEnd() ?? "(no process)";
            var exited = _process?.HasExited == true;
            var exitCode = _process?.ExitCode.ToString() ?? "?";
            throw new McpException(-1,
                $"MCP handshake timeout (15s). Process exited={exited} code={exitCode}. Stderr: {stderr[..Math.Min(stderr.Length, 500)]}");
        }

        if (initResult is not null)
        {
            ServerInfo = new McpServerInfo
            {
                Name = initResult["serverInfo"]?["name"]?.GetValue<string>() ?? "unknown",
                Version = initResult["serverInfo"]?["version"]?.GetValue<string>() ?? "unknown"
            };
        }

        // Send initialized notification (no response expected)
        await SendNotificationAsync("notifications/initialized", new JsonObject(), token.Token);

        // Discover tools
        var toolsResult = await SendRequestAsync("tools/list", new JsonObject(), token.Token);
        if (toolsResult?["tools"] is JsonArray toolsArr)
        {
            Tools.Clear();
            foreach (var t in toolsArr)
            {
                if (t is null)
                {
                    continue;
                }
                Tools.Add(new McpToolInfo
                {
                    Name = t["name"]?.GetValue<string>() ?? "",
                    Description = t["description"]?.GetValue<string>() ?? "",
                    InputSchema = t["inputSchema"]?.DeepClone()
                });
            }
        }

        _initialized = true;
        IsConnected = true;
    }

    /// <summary>
    /// Call a tool on the MCP server. Returns the text content of the response.
    /// </summary>
    public async Task<string> CallToolAsync(string toolName, JsonObject arguments, CancellationToken ct = default)
    {
        var result = await SendRequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments
        }, ct);

        if (result is null) return "(no response)";

        // Response format: { content: [{ type: "text", text: "..." }], isError?: bool }
        if (result["isError"]?.GetValue<bool>() == true)
            return "Error: " + ExtractText(result);

        return ExtractText(result);
    }

    private static string ExtractText(JsonNode node)
    {
        if (node["content"] is JsonArray contentArr)
        {
            var parts = new List<string>();
            foreach (var item in contentArr)
            {
                if (item is null)
                {
                    continue;
                }
                if (item["type"]?.GetValue<string>() == "text")
                    parts.Add(item["text"]?.GetValue<string>() ?? "");
            }
            return string.Join("\n", parts);
        }
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    // ── JSON-RPC core ──

    private async Task<JsonObject?> SendRequestAsync(string method, JsonObject @params, CancellationToken ct)
    {
        int id = _nextId++;
        DiagnosticsLog.Log($"MCP '{Name}': {method} begin");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params
        };

        var response = await SendAsync(request, ct);
        DiagnosticsLog.Log($"MCP '{Name}': {method} done ({sw.ElapsedMilliseconds}ms)");
        if (response is null) return null;

        if (response["error"] is JsonObject error)
        {
            throw new McpException(
                error["code"]?.GetValue<int>() ?? -1,
                error["message"]?.GetValue<string>() ?? "Unknown MCP error");
        }

        return response["result"] as JsonObject;
    }

    private async Task SendNotificationAsync(string method, JsonObject @params, CancellationToken ct)
    {
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params
        };
        // Notifications don't expect a response — just write, don't read
        if (_config.Transport == "stdio")
        {
            if (_process is null || _process.HasExited)
                throw new McpException(-1, "stdio process not running");
            string json = notification.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            await Task.Run(() =>
            {
                _process.StandardInput.WriteLine(json);
                _process.StandardInput.Flush();
            }, ct);
        }
        else
        {
            await SendHttpAsync(notification, ct);
        }
    }

    private async Task<JsonObject?> SendAsync(JsonObject message, CancellationToken ct)
    {
        if (_config.Transport == "stdio")
            return await SendStdioAsync(message, ct);
        return await SendHttpAsync(message, ct);
    }

    // ── stdio transport ──

    private async Task StartStdioAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.Command))
            throw new McpException(-1, $"No command configured for stdio MCP server '{_config.Name}'");

        var psi = new ProcessStartInfo
        {
            FileName = _config.Command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in _config.Args)
            psi.ArgumentList.Add(arg);

        foreach (var (key, val) in _config.Env)
            psi.Environment[key] = val;

        _process = new Process { StartInfo = psi };
        if (!_process.Start())
            throw new McpException(-1, $"Failed to start process: {_config.Command}");

        IsConnected = true;
    }

    private async Task<JsonObject?> SendStdioAsync(JsonObject message, CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
            throw new McpException(-1, $"stdio process not running (exited={_process?.HasExited}, code={_process?.ExitCode})");

        string json = message.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        // Write synchronously via Process.StandardInput (StreamWriter managed by Process)
        await Task.Run(() =>
        {
            _process.StandardInput.WriteLine(json);
            _process.StandardInput.Flush();
        }, ct);

        // Read response line (skip non-JSON lines)
        string? line;
        do
        {
            line = await _process.StandardOutput.ReadLineAsync(ct);
        } while (line is not null && !line.TrimStart().StartsWith('{'));

        if (line is null)
            throw new McpException(-1, "stdio process closed stdout without responding");
        return JsonNode.Parse(line) as JsonObject;
    }

    // ── HTTP transport ──

    private async Task<JsonObject?> SendHttpAsync(JsonObject message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.Url))
            throw new McpException(-1, $"No URL configured for HTTP MCP server '{_config.Name}'");

        var url = _config.Url.TrimEnd('/');
        var endpoint = url;

        using var content = new StringContent(
            message.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
            Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        // Include session ID if we have one (MCP Streamable HTTP sessions)
        if (!string.IsNullOrEmpty(_sessionId))
            request.Headers.Add("Mcp-Session-Id", _sessionId);

        using var response = await _http.SendAsync(request, ct);

        // Capture session ID from response (set during initialize)
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
        {
            _sessionId = sessionIds.FirstOrDefault();
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new McpException((int)response.StatusCode,
                $"HTTP {response.StatusCode}: {body[..Math.Min(body.Length, 200)]}");

        // Response might be JSON or SSE
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("event-stream"))
        {
            foreach (var line in body.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("data:"))
                {
                    var data = trimmed[5..].Trim();
                    if (data.StartsWith('{'))
                        return JsonNode.Parse(data) as JsonObject;
                }
            }
            return null;
        }

        return JsonNode.Parse(body) as JsonObject;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Send shutdown notification
            if (_initialized && IsConnected)
            {
                try { await SendNotificationAsync("notifications/cancelled", new JsonObject(), CancellationToken.None); }
                catch { /* best effort */ }
            }
        }
        finally
        {
            if (_process is not null)
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
                _process.Dispose();
            }
            _http.Dispose();
            IsConnected = false;
        }
    }
}

/// <summary>Info about a tool exposed by an MCP server.</summary>
public sealed class McpToolInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public JsonNode? InputSchema { get; set; }
}

/// <summary>Server info from MCP initialize response.</summary>
public sealed class McpServerInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>Exception for MCP protocol errors.</summary>
public sealed class McpException : Exception
{
    public int Code { get; }
    public McpException(int code, string message) : base(message) => Code = code;
}
