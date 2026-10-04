// UnityMcpRelay — replacement for Unity's relay_win.exe (which crashes on CPUs without AVX2).
//
// Speaks MCP (JSON-RPC 2.0, newline-delimited) on stdin/stdout for the AI client,
// and speaks the Unity MCP bridge protocol (newline-delimited JSON commands) over
// the editor's named pipe (\\.\pipe\unity-mcp-<projecthash>-<pid>).
//
// Bridge protocol (from com.unity.ai.assistant source, protocol v2.0):
//   connect -> bridge sends: {"type":"handshake","protocol":"unity-mcp","version":"2.0","toolsHash":"...","tools":[...]}
//   client may send:  {"type":"set_client_info","params":{"name","version","title"}}
//                     {"type":"get_available_tools","params":{"hash"}}
//                     {"type":"ping","requestId"}
//                     {"type":"<tool_name>","params":{...},"requestId"}   (any other type = tool call)
//   bridge sends:     {"status":"success"|"error","result":...,"error":...,"requestId":...}
//                     {"type":"command_in_progress"}  (heartbeat every 1.5s — ignore)
//                     {"type":"approval_pending"} / {"type":"approval_denied"}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

class BridgeInfo
{
    public string ConnectionPath { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public int EditorPid { get; set; }
    public DateTime Modified { get; set; }
}

static class Log
{
    static readonly object Lock = new();
    public static void Write(string msg)
    {
        lock (Lock)
            Console.Error.WriteLine($"[unity-mcp-relay {DateTime.Now:HH:mm:ss}] {msg}");
    }
}

class Bridge
{
    readonly string _pipeName;
    NamedPipeClientStream? _pipe;
    StreamReader? _reader;
    readonly object _writeLock = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    Task? _readerTask;
    volatile bool _connected;
    JsonElement? _tools;      // handshake tools array
    string? _toolsHash;
    TaskCompletionSource<bool>? _handshakeDone;

    public bool Connected => _connected;
    public JsonElement? Tools => _tools;
    public string? ToolsHash => _toolsHash;

    public Bridge(string pipePath)
    {
        // \\.\pipe\unity-mcp-xxx -> unity-mcp-xxx
        const string PipePrefix = @"\\.\pipe\";
        _pipeName = pipePath.StartsWith(PipePrefix) ? pipePath[PipePrefix.Length..] : pipePath;
    }

    public async Task<bool> ConnectAsync(TimeSpan timeout)
    {
        try
        {
            _handshakeDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, CancellationToken.None);
            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
            _connected = true;
            Log.Write($@"connected to pipe \\.\pipe\{_pipeName}");

            // Start reading only after _reader is assigned (otherwise the loop exits immediately)
            _readerTask = Task.Run(ReaderLoop);

            // Wait for handshake (bridge sends it right after connect)
            bool got = await _handshakeDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (!got)
            {
                Log.Write("handshake timeout");
                DisposePipe();
                return false;
            }

            // Identify ourselves (shows up in Unity's connection list)
            SendRaw(new { type = "set_client_info",
                          @params = new { name = "QwenPlayground", version = "1.0", title = "QwenPlayground Agent" } });

            // If handshake had no tools, request them
            if (_tools == null || _tools!.Value.GetArrayLength() == 0)
            {
                var resp = await CallAsync("get_available_tools", new { }, TimeSpan.FromSeconds(15));
                if (resp != null && resp.Value.TryGetProperty("result", out var r) && r.TryGetProperty("tools", out var t))
                {
                    _tools = t;
                    if (r.TryGetProperty("hash", out var h)) _toolsHash = h.GetString();
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"connect failed: {ex.Message}");
            DisposePipe();
            return false;
        }
    }

    void SendRaw(object msg)
    {
        if (_pipe == null || !_connected) return;
        var json = JsonSerializer.Serialize(msg);
        lock (_writeLock)
        {
            _pipe!.Write(Encoding.UTF8.GetBytes(json + "\n"));
            _pipe.Flush();
        }
    }

    async Task ReaderLoop()
    {
        while (_connected && _reader != null)
        {
            string? line;
            try { line = await _reader.ReadLineAsync(); }
            catch { break; }
            if (line == null) break; // pipe closed
            line = line.Trim();
            if (line.Length == 0) continue;

            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(line); }
            catch { Log.Write($"non-JSON line: {Trunc(line)}"); continue; }

            string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "handshake")
            {
                _toolsHash = root.TryGetProperty("toolsHash", out var th) ? th.GetString() : null;
                _tools = root.TryGetProperty("tools", out var tools) ? tools.Clone() : null;
                int n = _tools?.GetArrayLength() ?? 0;
                Log.Write($"handshake: protocol={(root.TryGetProperty("protocol", out var p) ? p.GetString() : "?")} " +
                          $"version={(root.TryGetProperty("version", out var v) ? v.GetString() : "?")} tools={n}");
                _handshakeDone?.TrySetResult(true);
                continue;
            }
            if (type == "command_in_progress") continue; // heartbeat
            if (type == "approval_pending") { Log.Write("approval pending — accept the connection in Unity (Project Settings > AI > Unity MCP Server)"); continue; }
            if (type == "approval_denied") { Log.Write($"approval DENIED: {(root.TryGetProperty("reason", out var rsn) ? rsn.GetString() : "?")}"); continue; }

            // Response to one of our commands
            if (root.TryGetProperty("requestId", out var rid) && root.TryGetProperty("status", out var st))
            {
                var id = rid.GetString() ?? "";
                if (_pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(root.Clone());
                continue;
            }
            // Anything else (e.g. status/error without requestId) — ignore
        }
        if (_connected)
        {
            Log.Write("bridge connection lost");
            _connected = false;
            foreach (var kv in _pending)
                kv.Value.TrySetResult(default); // signal failure; caller checks Connected
            _pending.Clear();
        }
    }

    public async Task<JsonElement?> CallAsync(string commandType, object? @params, TimeSpan timeout, string? requestId = null)
    {
        if (!Connected) return null;
        // B1: внешний requestId позволяет повторить вызов с тем же id после обрыва pipe —
        // мост дедуплицирует in-flight запросы по requestId и кэширует результаты 5 минут.
        var id = requestId ?? Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        SendRaw(new { type = commandType, @params, requestId = id });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        if (done != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            Log.Write($"timeout waiting for response to {commandType}");
            return null;
        }
        var resp = await tcs.Task;
        return resp.ValueKind == JsonValueKind.Undefined ? null : resp;
    }

    public void DisposePipe()
    {
        _connected = false;
        try { _reader?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _reader = null;
        _pipe = null;
    }

    static string Trunc(string s) => s.Length > 120 ? s[..120] + "…" : s;
}

static class Program
{
    static string? _projectPathFilter;
    static Bridge? _bridge;
    static readonly object BridgeLock = new();

    static async Task<int> Main(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--project-path") _projectPathFilter = args[i + 1];

        Log.Write($"starting (project filter: {_projectPathFilter ?? "any"})");

        // B2 (2026-10-04): короткий стартовый wait (~3 с). MCP-клиент имеет собственный
        // 15-с handshake-таймаут и автопереподключение, а ReconnectLoop (каждые 3 с)
        // ищет bridge в фоне. Длинный блокирующий wait на старте (было 10×3 с) отодвигал
        // stdio-цикл за таймаут клиента — сервер казался мёртвым, хотя bridge мог
        // появиться в любой момент.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            if (await TryConnectBridge()) break;
            await Task.Delay(1500);
        }

        _ = Task.Run(ReconnectLoop);

        // MCP stdio loop
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true, NewLine = "\n" };
        var enc = new UTF8Encoding(false);

        while (true)
        {
            string? line = await stdin.ReadLineAsync();
            if (line == null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            JsonElement req;
            try { req = JsonSerializer.Deserialize<JsonElement>(line); }
            catch { await WriteErrorAsync(stdout, enc, null, -32700, "Parse error"); continue; }

            JsonElement? id = req.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null ? idEl.Clone() : null;
            string method = req.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";

            try
            {
                switch (method)
                {
                    case "initialize":
                    {
                        var proto = req.TryGetProperty("params", out var p) && p.TryGetProperty("protocolVersion", out var pv)
                            ? pv.GetString() ?? "2025-03-26" : "2025-03-26";
                        await WriteResultAsync(stdout, enc, id, new
                        {
                            protocolVersion = proto,
                            capabilities = new { tools = new { listChanged = false } },
                            serverInfo = new { name = "unity-mcp-relay", version = "1.0.0" },
                            instructions = "Tools are exposed by the Unity Editor MCP bridge (com.unity.ai.assistant). " +
                                           "The first tool call may take a moment while the bridge connects."
                        });
                        break;
                    }
                    case "notifications/initialized":
                    case "initialized":
                        break; // notification, no reply
                    case "ping":
                        await WriteResultAsync(stdout, enc, id, new { });
                        break;
                    case "tools/list":
                        await HandleToolsListAsync(stdout, enc, id);
                        break;
                    case "tools/call":
                        await HandleToolCallAsync(stdout, enc, id, req);
                        break;
                    default:
                        if (id != null)
                            await WriteErrorAsync(stdout, enc, id, -32601, $"Method not found: {method}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"handler error for {method}: {ex}");
                if (id != null)
                    await WriteErrorAsync(stdout, enc, id, -32603, ex.Message);
            }
        }
        return 0;
    }

    static bool IsEditorAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    static string NormalizePath(string p) =>
        p.Trim().TrimEnd('\\', '/').Replace('/', '\\').ToUpperInvariant();

    static string? _lastMismatchLog;

    static BridgeInfo? FindBridge()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity", "mcp", "connections");
        if (!Directory.Exists(dir)) return null;

        BridgeInfo? best = null;
        var seenProjects = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "bridge-*.json"))
        {
            try
            {
                var json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file));
                var info = new BridgeInfo
                {
                    ConnectionPath = json.TryGetProperty("connection_path", out var cp) ? cp.GetString() ?? "" : "",
                    ProjectPath = json.TryGetProperty("project_path", out var pp) ? pp.GetString() ?? "" : "",
                    EditorPid = json.TryGetProperty("editor_pid", out var ep) ? ep.GetInt32() : 0,
                    Modified = File.GetLastWriteTime(file)
                };
                if (string.IsNullOrEmpty(info.ConnectionPath)) continue;
                seenProjects.Add(info.ProjectPath);
                if (!IsEditorAlive(info.EditorPid)) continue;
                if (_projectPathFilter != null &&
                    NormalizePath(info.ProjectPath) != NormalizePath(_projectPathFilter)) continue;
                if (best == null || info.Modified > best.Modified) best = info;
            }
            catch { /* skip bad file */ }
        }

        // A2: filter не совпал ни с одним bridge (проект перенесли? редактор на другой
        // копии?) — явное сообщение вместо молчаливого «no bridge». Лог только при
        // изменении: ReconnectLoop зовёт FindBridge каждые 3 с.
        if (best == null && _projectPathFilter != null && seenProjects.Count > 0)
        {
            var msg = "project filter '" + _projectPathFilter + "' matches no bridge; found projects: " +
                      string.Join("; ", seenProjects.Distinct());
            if (msg != _lastMismatchLog)
            {
                Log.Write(msg);
                _lastMismatchLog = msg;
            }
        }
        else if (best != null)
        {
            _lastMismatchLog = null;
        }

        return best;
    }

    static async Task<bool> TryConnectBridge()
    {
        var info = FindBridge();
        if (info == null) return false;
        Log.Write($"found bridge: {info.ConnectionPath} (pid {info.EditorPid}, project {info.ProjectPath})");
        var bridge = new Bridge(info.ConnectionPath);
        bool ok = await bridge.ConnectAsync(TimeSpan.FromSeconds(10));
        if (ok)
        {
            lock (BridgeLock) _bridge = bridge;
        }
        else bridge.DisposePipe();
        return ok;
    }

    static async Task ReconnectLoop()
    {
        while (true)
        {
            await Task.Delay(3000);
            bool need;
            lock (BridgeLock) need = _bridge == null || !_bridge.Connected;
            if (!need) continue;
            lock (BridgeLock) _bridge?.DisposePipe();
            if (await TryConnectBridge())
                Log.Write("reconnected to Unity bridge");
        }
    }

    static async Task EnsureBridgeAsync(TimeSpan? cap = null)
    {
        // Domain reloads take ~37s on this machine — wait generously and try
        // connecting directly, so a tool call issued DURING a reload simply
        // blocks until the bridge is back (no client-side sleep rituals).
        // B2: tools/list использует короткий cap (5 с) — у MCP-клиента свой
        // 15-с handshake-таймаут и автопереподключение, длинный hang бесполезен.
        var limit = cap ?? TimeSpan.FromSeconds(120);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            Bridge? b;
            lock (BridgeLock) b = _bridge;
            if (b != null && b.Connected) return;
            if (await TryConnectBridge())
            {
                Log.Write($"bridge ready after {sw.Elapsed.TotalSeconds:F0}s");
                return;
            }
            await Task.Delay(2000);
        }
    }

    static async Task HandleToolsListAsync(StreamWriter stdout, Encoding enc, JsonElement? id)
    {
        // B2: короткий wait (5 с вместо 120 с). У MCP-клиента 15-с handshake-таймаут
        // и автопереподключение: быстрый error → клиент переподключит сервер сам,
        // а 120-с hang превращался в timeout + kill процесса relay.
        await EnsureBridgeAsync(TimeSpan.FromSeconds(5));
        Bridge? b;
        lock (BridgeLock) b = _bridge;
        var tools = b?.Tools;
        if (tools == null || tools.Value.ValueKind != JsonValueKind.Array)
        {
            await WriteErrorAsync(stdout, enc, id, -32000, "Not connected to Unity MCP bridge (is the editor open?)");
            return;
        }
        var list = new List<object>();
        foreach (var t in tools.Value.EnumerateArray())
        {
            list.Add(new
            {
                name = t.TryGetProperty("name", out var n) ? n.GetString() : null,
                description = t.TryGetProperty("description", out var d) ? d.GetString() : null,
                inputSchema = t.TryGetProperty("inputSchema", out var s) && s.ValueKind != JsonValueKind.Null
                    ? s.Clone() : JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
            });
        }
        await WriteResultAsync(stdout, enc, id, new { tools = list });
    }

    static async Task HandleToolCallAsync(StreamWriter stdout, Encoding enc, JsonElement? id, JsonElement req)
    {
        if (!req.TryGetProperty("params", out var p))
        {
            await WriteErrorAsync(stdout, enc, id, -32602, "Missing params");
            return;
        }
        var name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var arguments = p.TryGetProperty("arguments", out var a) ? a.Clone() : JsonSerializer.Deserialize<JsonElement>("{}");

        // B1 (2026-10-04): слепой retry после смерти bridge убран. Старая логика
        // повторно отправляла запрос после domain reload — для неидемпотентных
        // тулов (RunCommand!) это означало ДВОЙНОЕ исполнение. Теперь:
        // - мост дедуплицирует in-flight запросы по requestId и кэширует результаты
        //   5 минут, поэтому повтор с ТЕМ ЖЕ requestId безопасен, если мост НЕ
        //   рестартовал (обрыв pipe);
        // - если мост рестартовал (domain reload: discovery-файл переписан с новым
        //   created_date), кэш снесён и повтор = переисполнение — НЕ повторяем,
        //   возвращаем явную ошибку и даём модели решать.
        var requestId = Guid.NewGuid().ToString("N");
        var startInfo = FindBridge();
        await EnsureBridgeAsync();
        Bridge? b;
        lock (BridgeLock) b = _bridge;
        if (b == null || !b.Connected)
        {
            await WriteErrorAsync(stdout, enc, id, -32000, "Not connected to Unity MCP bridge (is the editor open?)");
            return;
        }

        // Таймаут 1800 с (2026-10-05): длинные одиночные вызовы с наблюдением (sim_run:
        // до 900 с симуляции + периферия) должны проходить без таймаута. Зависший
        // синхронный тул теперь блокирует до 30 мин — принимается: редактор при этом
        // либо жив (async-тулы), либо замёрз целиком и виден владельцу.
        Log.Write($"tool call: {name} (req {requestId[..8]})");
        var resp = await b.CallAsync(name, arguments, TimeSpan.FromSeconds(1800), requestId);
        if (resp == null)
        {
            bool alive;
            lock (BridgeLock) alive = _bridge?.Connected ?? false;
            if (alive)
            {
                await WriteErrorAsync(stdout, enc, id, -32000,
                    $"Unity bridge timed out after 1800s on '{name}' (bridge alive — the operation may still be running on the editor's main thread)");
                return;
            }

            // Bridge умер во время вызова. Ждём переподключения.
            Log.Write($"tool call {name} lost (bridge died) — waiting for reconnect");
            await EnsureBridgeAsync();
            lock (BridgeLock) b = _bridge;
            if (b == null || !b.Connected)
            {
                await WriteErrorAsync(stdout, enc, id, -32000,
                    $"Unity bridge was lost during the call '{name}' and did not come back within 120s (is the editor open?)");
                return;
            }

            // Мост рестартовал? Новый created_date discovery-файла / новый PID =
            // domain reload или рестарт редактора → кэш результатов снесён →
            // повтор = переисполнение операции.
            var nowInfo = FindBridge();
            bool restarted = nowInfo == null || startInfo == null
                || nowInfo.EditorPid != startInfo.EditorPid
                || nowInfo.Modified != startInfo.Modified;
            if (restarted)
            {
                await WriteErrorAsync(stdout, enc, id, -32000,
                    $"Unity bridge restarted (domain reload) during the call '{name}': the operation was interrupted. " +
                    "The editor is ready now — retry the call explicitly if the operation is safe to re-run.");
                return;
            }

            // Мост жив и НЕ рестартовался (обрыв pipe): повтор с тем же requestId —
            // дедуп/кэш результатов моста гарантирует отсутствие двойного исполнения.
            Log.Write($"bridge back without restart — retrying {name} with same requestId (cache-safe)");
            resp = await b.CallAsync(name, arguments, TimeSpan.FromSeconds(1800), requestId);
            if (resp == null)
            {
                await WriteErrorAsync(stdout, enc, id, -32000, "No response from Unity bridge on retry (timeout or disconnected)");
                return;
            }
        }

        var r = resp.Value;
        bool isError = r.TryGetProperty("status", out var st) && st.GetString() == "error";
        string text;
        if (isError)
            text = r.TryGetProperty("error", out var e) ? e.ToString() : r.ToString();
        else
            text = r.TryGetProperty("result", out var res) ? res.ToString() : "{}";

        await WriteResultAsync(stdout, enc, id, new
        {
            content = new[] { new { type = "text", text } },
            isError
        });
    }

    static async Task WriteResultAsync(StreamWriter stdout, Encoding enc, JsonElement? id, object result)
    {
        var msg = new { jsonrpc = "2.0", id, result = result is JsonElement je ? je : JsonSerializer.SerializeToElement(result) };
        var line = JsonSerializer.Serialize(msg, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        await stdout.WriteLineAsync(line);
    }

    static async Task WriteErrorAsync(StreamWriter stdout, Encoding enc, JsonElement? id, int code, string message)
    {
        var msg = new { jsonrpc = "2.0", id, error = new { code, message } };
        var line = JsonSerializer.Serialize(msg, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        await stdout.WriteLineAsync(line);
    }
}
