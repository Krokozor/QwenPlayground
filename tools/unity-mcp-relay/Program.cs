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

    public async Task<JsonElement?> CallAsync(string commandType, object? @params, TimeSpan timeout)
    {
        if (!Connected) return null;
        var id = Guid.NewGuid().ToString("N");
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

        // Discover + connect with retries (Unity may still be starting)
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            if (await TryConnectBridge()) break;
            if (attempt == 10)
                Log.Write("no Unity MCP bridge found after retries — will keep trying in background");
            await Task.Delay(3000);
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

    static BridgeInfo? FindBridge()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity", "mcp", "connections");
        if (!Directory.Exists(dir)) return null;

        BridgeInfo? best = null;
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
                if (!IsEditorAlive(info.EditorPid)) continue;
                if (_projectPathFilter != null &&
                    NormalizePath(info.ProjectPath) != NormalizePath(_projectPathFilter)) continue;
                if (best == null || info.Modified > best.Modified) best = info;
            }
            catch { /* skip bad file */ }
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

    static async Task EnsureBridgeAsync()
    {
        for (int i = 0; i < 20; i++) // up to ~30s
        {
            Bridge? b;
            lock (BridgeLock) b = _bridge;
            if (b != null && b.Connected) return;
            await Task.Delay(1500);
        }
    }

    static async Task HandleToolsListAsync(StreamWriter stdout, Encoding enc, JsonElement? id)
    {
        await EnsureBridgeAsync();
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

        await EnsureBridgeAsync();
        Bridge? b;
        lock (BridgeLock) b = _bridge;
        if (b == null || !b.Connected)
        {
            await WriteErrorAsync(stdout, enc, id, -32000, "Not connected to Unity MCP bridge (is the editor open?)");
            return;
        }

        Log.Write($"tool call: {name}");
        var resp = await b.CallAsync(name, arguments, TimeSpan.FromSeconds(180));
        if (resp == null)
        {
            await WriteErrorAsync(stdout, enc, id, -32000, "No response from Unity bridge (timeout or disconnected)");
            return;
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
