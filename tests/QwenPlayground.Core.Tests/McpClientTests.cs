using System.Diagnostics;
using QwenPlayground.Core.Mcp;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Поведение McpClient при некорректных stdio-серверах. Ключевой сценарий: сервер,
/// пишущий в stderr больше пайп-буфера (~4 КБ) и не отвечающий на handshake.
/// Старый код: (1) сервер блокировался на write в stderr (никто не читал),
/// (2) при таймауте клиент делал блокирующий ReadToEnd на UI-потоке,
/// (3) осиротевший процесс оставался жить. Новый код: фоновый насос stderr
/// (полный захват в logs/mcp/&lt;name&gt;.stderr.log), неблокирующая диагностика в
/// ошибке, kill процесса при любом сбое.
/// </summary>
public class McpClientTests
{
    /// <summary>Python доступен в окружении (тесты запускаются на машине разработчика).</summary>
    private static bool PythonAvailable()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("python", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task HangingServer_WithChattyStderr_TimesOut_CapturesStderr_KillsProcess()
    {
        if (!PythonAvailable()) return; // нет python — сценарий не прогнать

        var dir = Path.Combine(Path.GetTempPath(), "qpw_mcp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "probe.py");
        var pidFile = Path.Combine(dir, "pid.txt");
        // 200 строк × ~60 символов ≈ 12 КБ — больше пайп-буфера: без дренажа stderr
        // сервер заблокировался бы на write и тест бы завис намертво.
        File.WriteAllText(script,
            "import os, sys, time\n" +
            "with open(sys.argv[1], 'w') as f:\n" +
            "    f.write(str(os.getpid()))\n" +
            "for i in range(200):\n" +
            "    print('stderr line %d: ' % i + 'x' * 60, file=sys.stderr, flush=True)\n" +
            "time.sleep(600)\n");

        var serverName = "probe_hang_" + Guid.NewGuid().ToString("N")[..8];
        McpClient? client = null;
        try
        {
            var config = new McpServerConfig
            {
                Name = serverName,
                Enabled = true,
                Transport = "stdio",
                Command = "python",
                Args = { script, pidFile }
            };
            client = new McpClient(config);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<McpException>(() => client.InitializeAsync());
            sw.Stop();

            // Таймаут handshake ≈ 15 с (не мгновенно и не бесконечно).
            Assert.InRange(sw.Elapsed, TimeSpan.FromSeconds(14), TimeSpan.FromSeconds(40));
            Assert.Contains("handshake timeout", ex.Message);

            // Диагностика в ошибке: stderr-хвост + путь к полному логу.
            Assert.Contains("stderr", ex.Message);
            Assert.Contains("stderr.log", ex.Message);

            // Полный захват stderr: все 200 строк в файле (старый код — deadlock).
            var logPath = client.StderrLogPath;
            Assert.NotNull(logPath);
            Assert.True(client.StderrLineCount >= 200, $"stderr lines: {client.StderrLineCount}");
            var lines = File.ReadAllLines(logPath!);
            Assert.True(lines.Length >= 200, $"log lines: {lines.Length}");
            Assert.Contains("stderr line 199", string.Join("\n", lines));

            // Процесс убит — осиротевших не остаётся.
            var pid = int.Parse(File.ReadAllText(pidFile).Trim());
            bool alive;
            try
            {
                using var proc = Process.GetProcessById(pid);
                alive = !proc.HasExited;
            }
            catch (ArgumentException)
            {
                alive = false; // процесса нет — как и надо
            }
            Assert.False(alive, $"probe process {pid} still alive after failed handshake");

            await client!.DisposeAsync();
        }
        finally
        {
            try { if (client is not null && client.StderrLogPath is not null) File.Delete(client.StderrLogPath); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WellBehavedServer_Connects_AndMatchesResponseById()
    {
        if (!PythonAvailable()) return;

        var dir = Path.Combine(Path.GetTempPath(), "qpw_mcp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "ok.py");
        // Сервер-хорошист: перед каждым ответом шлёт нотификацию (без id) —
        // клиент обязан пропустить её и дождаться ответа СВОИМ id.
        File.WriteAllText(script,
            "import json, sys\n" +
            "def send(o): sys.stdout.write(json.dumps(o) + '\\n'); sys.stdout.flush()\n" +
            "send({'jsonrpc': '2.0', 'method': 'notifications/message', 'params': {'data': 'hello'}})\n" +
            "while True:\n" +
            "    line = sys.stdin.readline()\n" +
            "    if not line: break\n" +
            "    req = json.loads(line)\n" +
            "    rid = req.get('id')\n" +
            "    if rid is None: continue\n" +
            "    m = req.get('method')\n" +
            "    if m == 'initialize':\n" +
            "        send({'jsonrpc': '2.0', 'id': rid, 'result': {'protocolVersion': '2024-11-05', 'capabilities': {}, 'serverInfo': {'name': 'ok', 'version': '1'}}})\n" +
            "    elif m == 'tools/list':\n" +
            "        send({'jsonrpc': '2.0', 'id': rid, 'result': {'tools': [{'name': 'ping', 'description': 'p', 'inputSchema': {'type': 'object', 'properties': {}}}]}})\n" +
            "    elif m == 'tools/call':\n" +
            "        send({'jsonrpc': '2.0', 'id': rid, 'result': {'content': [{'type': 'text', 'text': 'pong'}]}})\n");

        var serverName = "probe_ok_" + Guid.NewGuid().ToString("N")[..8];
        McpClient? client = null;
        try
        {
            var config = new McpServerConfig
            {
                Name = serverName,
                Enabled = true,
                Transport = "stdio",
                Command = "python",
                Args = { script }
            };
            client = new McpClient(config);
            await client.InitializeAsync();

            Assert.True(client.IsConnected);
            Assert.Equal("ok", client.ServerInfo?.Name);
            var tools = client.Tools.Select(t => t.Name).ToList();
            Assert.Contains("ping", tools);

            var result = await client.CallToolAsync("ping", new System.Text.Json.Nodes.JsonObject());
            Assert.Equal("pong", result);

            await client!.DisposeAsync();
        }
        finally
        {
            try { if (client is not null && client.StderrLogPath is not null) File.Delete(client.StderrLogPath); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
