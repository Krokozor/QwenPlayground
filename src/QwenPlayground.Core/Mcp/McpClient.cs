using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Mcp;

/// <summary>
/// MCP (Model Context Protocol) client. Supports stdio and HTTP transports.
/// Implements the JSON-RPC 2.0 protocol as specified by MCP.
///
/// Диагностика (принцип: ничего не глотать молча):
/// - stderr сервера — его диагностический канал (по спеке протокол только по stdout).
///   Каждое stdio-подключение поднимает фоновый насос: полный захват в
///   logs/mcp/&lt;сервер&gt;.stderr.log (ротация при 1 МБ) + хвост в памяти для ошибок.
/// - Ответ сопоставляется по id запроса; чужие строки stdout (нотификации, запросы
///   от сервера, мусор) не теряются — пишутся в DiagnosticsLog, первый случай — на доску
///   анонсов (виден в state-блоке).
/// - Любой сбой после старта процесса → kill дерева (нет осиротевших процессов);
///   в текст ошибки попадают код выхода, хвост stderr и путь к полному логу.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    /// <summary>Бюджет хвоста stderr в памяти (для текста ошибок); полный лог — на диске.</summary>
    private const int StderrTailBudget = 8192;

    /// <summary>Лимит одного файла stderr-лога; дальше — ротация в .1 (старое отбрасывается).</summary>
    private const long StderrLogMaxBytes = 1024 * 1024;

    private readonly McpServerConfig _config;
    private readonly HttpClient _http;
    private Process? _process;
    private Task? _stderrPump;
    private readonly object _stderrTailLock = new();
    private readonly Queue<string> _stderrTail = new();
    private int _stderrTailBytes;
    private int _stderrLines;
    private string? _stderrLogPath;
    private int _skippedNoted;
    private bool _shutdownInitiated;
    private int _nextId = 1;
    private bool _initialized;
    private string? _sessionId;

    /// <summary>Tools discovered from this server.</summary>
    public List<McpToolInfo> Tools { get; } = new();

    /// <summary>Server info from initialize response.</summary>
    public McpServerInfo? ServerInfo { get; private set; }

    public string Name => _config.Name;
    public string Transport => _config.Transport;
    public bool IsConnected { get; private set; }

    /// <summary>Сколько строк сервер написал в stderr с момента подключения.</summary>
    public int StderrLineCount => Volatile.Read(ref _stderrLines);

    /// <summary>Путь к полному stderr-логу (stdio-серверы; null, пока не запущен процесс).</summary>
    public string? StderrLogPath => _stderrLogPath;

    public McpClient(McpServerConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Connect to the MCP server: launch process (stdio) or set up HTTP,
    /// then perform the initialize handshake and discover tools.
    /// Times out after 15 seconds if the server doesn't respond.
    /// Любой сбой после запуска процесса → kill (осиротевший процесс не остаётся).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var token = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        if (_config.Transport == "stdio")
            StartStdio();
        else
            IsConnected = true;

        try
        {
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
                // Таймаут — диагностика из НЕБЛОКИРУЮЩИХ источников (насос stderr уже всё ловит).
                throw new McpException(-1,
                    $"MCP handshake timeout (15s). {ProcessDiagnostics()}");
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
        finally
        {
            // Любая неудача (таймаут, ошибка протокола, процесс умер) — процесс не остаётся.
            if (!_initialized)
                KillProcess();
        }
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
        int id = Interlocked.Increment(ref _nextId);
        DiagnosticsLog.Log($"MCP '{Name}': {method} begin");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params
        };

        var response = await SendAsync(request, id, ct);
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

    private async Task<JsonObject?> SendAsync(JsonObject message, int id, CancellationToken ct)
    {
        if (_config.Transport == "stdio")
            return await SendStdioAsync(message, id, ct);
        return await SendHttpAsync(message, ct);
    }

    // ── stdio transport ──

    private void StartStdio()
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

        var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new McpException(-1, $"Failed to start process: {_config.Command}");

        _process = process;
        IsConnected = true;

        // Насос stderr: без него сервер, написавший в stderr больше пайп-буфера (~4 КБ),
        // блокируется на write и виснет. Полный захват в файл + хвост в памяти.
        try
        {
            Directory.CreateDirectory(StderrLogDirectory);
            _stderrLogPath = Path.Combine(StderrLogDirectory, SanitizeFileName(_config.Name) + ".stderr.log");
            _stderrPump = Task.Run(() => PumpStderrAsync(process));
        }
        catch (Exception ex)
        {
            // Насос не критичен для протокола, но его отсутствие — потеря диагностики: фиксируем.
            DiagnosticsLog.Log($"MCP '{Name}': stderr pump failed to start: {ex.Message}");
        }
    }

    private static string StderrLogDirectory =>
        Path.Combine(SelfBuildPaths.WorkspaceRoot, "logs", "mcp");

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars);
        return result.Length == 0 ? "server" : result;
    }

    /// <summary>
    /// Фоновый дренаж stderr до EOF процесса: каждая строка — в лог-файл (полный захват,
    /// ротация при лимите) и в хвост в памяти. Никогда не бросает: смерть насоса не должна
    /// влиять на протокол.
    /// </summary>
    private async Task PumpStderrAsync(Process process)
    {
        try
        {
            if (_stderrLogPath is null) return;
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                await AppendStderrLogAsync(line);
                OnStderrLine(line);
            }
        }
        catch
        {
            // Смерть процесса / закрытый поток — нормальный конец насоса.
        }

        // EOF stderr: процесс ушёл. Если сессия была жива — это неожиданный выход.
        if (_initialized && !_shutdownInitiated)
        {
            int? code = null;
            try { if (process.HasExited) code = process.ExitCode; } catch { }
            DiagnosticsLog.Log($"MCP '{Name}': process exited during session" + (code is null ? "" : $" (code {code})"));
            AnnouncementBoard.Push("mcp:" + Name,
                "процесс сервера завершился во время сессии" + (code is null ? "" : $" (код {code})") + $" — stderr: {StderrLogPath}");
        }
    }

    /// <summary>Дописывает строку в stderr-лог; при превышении лимита — ротация в .1.</summary>
    private Task AppendStderrLogAsync(string line)
    {
        var path = _stderrLogPath;
        if (path is null) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }
                if (size > StderrLogMaxBytes)
                {
                    // Ротация: текущий → .1 (старое .1 отбрасывается), начинаем заново.
                    var rotated = path + ".1";
                    if (File.Exists(rotated)) File.Delete(rotated);
                    File.Move(path, rotated);
                }
                await File.AppendAllTextAsync(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
            }
            catch
            {
                // Запись лога best-effort: насос не должен умирать из-за диска.
            }
        });
    }

    private void OnStderrLine(string line)
    {
        Interlocked.Increment(ref _stderrLines);
        lock (_stderrTailLock)
        {
            _stderrTail.Enqueue(line);
            _stderrTailBytes += line.Length + 1;
            while (_stderrTailBytes > StderrTailBudget && _stderrTail.Count > 1)
            {
                _stderrTailBytes -= _stderrTail.Dequeue().Length + 1;
            }
        }
        DiagnosticsLog.Log($"MCP '{Name}': stderr: {Truncate(line, 200)}");
    }

    /// <summary>Хвост stderr для сообщений об ошибках (последние строки, без таймстампов).</summary>
    public string StderrTail(int maxChars = 500)
    {
        lock (_stderrTailLock)
        {
            var all = string.Join("\n", _stderrTail);
            return all.Length <= maxChars ? all : all[^maxChars..];
        }
    }

    /// <summary>Диагностика процесса для сообщений об ошибках (неблокирующая).</summary>
    private string ProcessDiagnostics()
    {
        var parts = new List<string>();
        if (_process is { } p)
        {
            bool exited;
            int? code = null;
            try
            {
                exited = p.HasExited;
                if (exited) code = p.ExitCode;
            }
            catch
            {
                exited = false;
            }
            parts.Add($"process exited={exited}" + (code is null ? "" : $" code={code}"));
        }
        else
        {
            parts.Add("no process");
        }

        int lines = StderrLineCount;
        var tail = StderrTail();
        if (lines > 0)
            parts.Add($"stderr ({lines} lines, last): {Truncate(tail, 500)}");
        if (_stderrLogPath is not null)
            parts.Add($"full stderr log: {_stderrLogPath}");

        return string.Join(" | ", parts);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[^max..];

    private async Task<JsonObject?> SendStdioAsync(JsonObject message, int id, CancellationToken ct)
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

        // Читаем до ответа С НАШИМ id. Чужие строки (нотификации/запросы от сервера, мусор)
        // не глотаем молча: DiagnosticsLog + первый случай — на доску анонсов.
        int skipped = 0;
        while (true)
        {
            string? line = await _process.StandardOutput.ReadLineAsync(ct);
            if (line is null)
                throw new McpException(-1,
                    $"stdio process closed stdout without responding (id={id}, skipped {skipped} line(s). {ProcessDiagnostics()})");

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith('{'))
            {
                LogSkippedLine(line, "non-JSON");
                skipped++;
                continue;
            }

            JsonObject? obj;
            try
            {
                obj = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                obj = null;
            }
            if (obj is null)
            {
                LogSkippedLine(line, "not a JSON object");
                skipped++;
                continue;
            }

            var respId = obj["id"];
            if (respId is null)
            {
                // id нет — нотификация или запрос от сервера, не ответ на наш запрос.
                LogSkippedLine(line, $"server→client ({obj["method"]?.GetValue<string>() ?? "no method"})");
                skipped++;
                continue;
            }

            int theirId;
            try
            {
                theirId = respId.GetValue<int>();
            }
            catch
            {
                theirId = -1;
            }
            if (theirId != id)
            {
                LogSkippedLine(line, $"id={theirId} != {id}");
                skipped++;
                continue;
            }

            return obj;
        }
    }

    private void LogSkippedLine(string line, string reason)
    {
        DiagnosticsLog.Log($"MCP '{Name}': skipped stdout line ({reason}): {Truncate(line, 200)}");
        if (Interlocked.Exchange(ref _skippedNoted, 1) == 0)
        {
            AnnouncementBoard.Push("mcp:" + Name,
                $"сервер шлёт неожиданные сообщения по stdout ({reason}) — детали в logs/diag-*.log");
        }
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
            // Числовой код, а не только имя enum: «InternalServerError» в тексте ошибки
            // требует знания таблицы статусов, «500» читается сразу.
            throw new McpException((int)response.StatusCode,
                $"HTTP {(int)response.StatusCode} ({response.StatusCode}): {body[..Math.Min(body.Length, 200)]}");

        // 202 с пустым телом — нормальный ответ на уведомление по спеке Streamable HTTP
        // (сервер подтверждает приём, результата нет). Без этой проверки JsonNode.Parse("")
        // падала бы на каждом notifications/* запросе.
        if (string.IsNullOrWhiteSpace(body))
            return null;

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
        _shutdownInitiated = true;
        // MCP не имеет «shutdown»-запроса (notifications/cancelled требует requestId —
        // слать его с пустыми params — нарушение протокола). Корректное завершение:
        // закрыть stdin (сервер увидит EOF) и дать процессу уйти самому; не уйдёт — kill.
        if (_process is { } p)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.StandardInput.Close();
                    if (!p.WaitForExit(3000))
                        p.Kill(entireProcessTree: true);
                }
            }
            catch { /* best effort */ }
        }

        if (_stderrPump is { } pump)
        {
            try { await pump.WaitAsync(CancellationToken.None); }
            catch { /* насос может быть уже мёртв */ }
        }

        KillProcess();
        _http.Dispose();
        IsConnected = false;
    }

    /// <summary>Убивает процесс (дерево) и освобождает ресурсы. Идемпотентно.</summary>
    private void KillProcess()
    {
        if (_process is not null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { _process.Dispose(); } catch { }
            _process = null;
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

/// <summary>Server info from MCP server initialize response.</summary>
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
