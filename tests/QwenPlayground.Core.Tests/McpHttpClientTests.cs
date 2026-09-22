using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Mcp;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Локальный MCP-сервер по HTTP (Streamable HTTP) для проверки HTTP-транспорта McpClient.
/// Живьём транспорт испытывали только на HF (connect+call), путь dispose не покрывался —
/// именно там жил NRE на await null stderr-насоса. Режимы: Normal (JSON),
/// Sse (ответы text/event-stream — отдельная ветка парсинга клиента), Hang (не отвечает),
/// Error500. На уведомления сервер отвечает 202 с ПУСТЫМ телом — по спеке; без этого
/// клиент падал на JsonNode.Parse("") (баг пойман этим тестом).
/// </summary>
public sealed class HttpMcpTestServer : IAsyncDisposable
{
    public enum Mode { Normal, Sse, Hang, Error500 }

    private readonly HttpListener _listener;
    private readonly Task _loop;
    private readonly Mode _mode;
    private readonly List<string> _receivedMethods = new();
    private readonly List<bool> _receivedSessionHeaders = new();

    public string Url { get; }
    public IReadOnlyList<string> ReceivedMethods { get { lock (_receivedMethods) return _receivedMethods.ToList(); } }
    public IReadOnlyList<bool> ReceivedSessionHeaders { get { lock (_receivedSessionHeaders) return _receivedSessionHeaders.ToList(); } }

    public static HttpMcpTestServer Start(Mode mode = Mode.Normal)
    {
        // HttpListener не понимает порт 0 — свободный порт берём через TcpListener.
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var url = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();
        return new HttpMcpTestServer(listener, mode, url);
    }

    private HttpMcpTestServer(HttpListener listener, Mode mode, string url)
    {
        _listener = listener;
        _mode = mode;
        Url = url;
        _loop = RunAsync();
    }

    private async Task RunAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return; // listener.Stop()
            }
            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (_mode is Mode.Hang)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }
            var request = JsonNode.Parse(body)?.AsObject();
            var method = request?["method"]?.GetValue<string>() ?? "?";
            lock (_receivedMethods)
            {
                _receivedMethods.Add(method);
                _receivedSessionHeaders.Add(ctx.Request.Headers["Mcp-Session-Id"] is not null);
            }

            if (_mode is Mode.Error500)
            {
                ctx.Response.StatusCode = 500;
                await WriteAsync(ctx, "application/json", "boom");
                return;
            }

            // Уведомление (нет id): 202 с пустым телом — по спеке Streamable HTTP.
            if (request?["id"] is null)
            {
                ctx.Response.StatusCode = 202;
                ctx.Response.Close();
                return;
            }

            var id = request!["id"]!.GetValue<int>();
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = BuildResult(method, request!["params"] as JsonObject)
            };
            if (method == "initialize")
                ctx.Response.Headers.Add("Mcp-Session-Id", "test-session-123");
            if (_mode is Mode.Sse)
                await WriteAsync(ctx, "text/event-stream", $"data: {response.ToJsonString()}\n\n");
            else
                await WriteAsync(ctx, "application/json", response.ToJsonString());
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* сервер закрывается */ }
        }
    }

    private static JsonObject BuildResult(string method, JsonObject? @params) => method switch
    {
        "initialize" => new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["serverInfo"] = new JsonObject { ["name"] = "http-test-server", ["version"] = "9.9" }
        },
        "tools/list" => new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "echo",
                    ["description"] = "echo the text",
                    ["inputSchema"] = new JsonObject { ["type"] = "object" }
                }
            }
        },
        "tools/call" => new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = @params?["arguments"]?["text"]?.GetValue<string>() ?? ""
                }
            }
        },
        _ => new JsonObject()
    };

    private static async Task WriteAsync(HttpListenerContext ctx, string contentType, string body)
    {
        ctx.Response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        try { _listener.Stop(); } catch { /* уже остановлен */ }
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* цикл не успел уйти */ }
    }
}

/// <summary>
/// HTTP-транспорт McpClient: полный lifecycle (connect → handshake → tools/call → dispose),
/// SSE-ветка парсинга, висящий сервер (таймаут handshake), ошибка сервера (500).
/// </summary>
public class McpHttpClientTests
{
    private static McpClient CreateClient(string url) => new(new McpServerConfig
    {
        Name = "http_test_" + Guid.NewGuid().ToString("N")[..8],
        Enabled = true,
        Transport = "http",
        Url = url
    });

    [Fact]
    public async Task Http_HappyLifecycle_Connect_Call_Dispose()
    {
        await using var server = HttpMcpTestServer.Start();
        await using var client = CreateClient(server.Url);

        await client.InitializeAsync();

        Assert.True(client.IsConnected);
        Assert.Equal("http-test-server", client.ServerInfo?.Name);
        Assert.Contains("echo", client.Tools.Select(t => t.Name));

        // Порядок handshake + сессия: клиент обязан вернуть Mcp-Session-Id из
        // ответа initialize в последующих запросах (Streamable HTTP sessions).
        Assert.Equal(new[] { "initialize", "notifications/initialized", "tools/list" }, server.ReceivedMethods);
        Assert.Equal(new[] { false, true, true }, server.ReceivedSessionHeaders);

        var result = await client.CallToolAsync("echo", new JsonObject { ["text"] = "hello world" });
        Assert.Equal("hello world", result);
        // dispose не должен падать — здесь жил NRE на await null stderr-насоса.
    }

    [Fact]
    public async Task Http_SseResponses_ParsedCorrectly()
    {
        await using var server = HttpMcpTestServer.Start(HttpMcpTestServer.Mode.Sse);
        await using var client = CreateClient(server.Url);

        await client.InitializeAsync();
        Assert.Equal("http-test-server", client.ServerInfo?.Name);

        var result = await client.CallToolAsync("echo", new JsonObject { ["text"] = "via sse" });
        Assert.Equal("via sse", result);
    }

    [Fact]
    public async Task Http_HangingServer_InitializeTimesOut()
    {
        await using var server = HttpMcpTestServer.Start(HttpMcpTestServer.Mode.Hang);
        await using var client = CreateClient(server.Url);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<McpException>(() => client.InitializeAsync());
        sw.Stop();

        // Таймаут handshake ≈ 15 с (не мгновенно и не бесконечно).
        Assert.InRange(sw.Elapsed, TimeSpan.FromSeconds(14), TimeSpan.FromSeconds(40));
        Assert.Contains("handshake timeout", ex.Message);
    }

    [Fact]
    public async Task Http_ServerError_SurfacesStatusAndBody()
    {
        await using var server = HttpMcpTestServer.Start(HttpMcpTestServer.Mode.Error500);
        await using var client = CreateClient(server.Url);

        var ex = await Assert.ThrowsAsync<McpException>(() => client.InitializeAsync());
        Assert.Equal(500, ex.Code);
        Assert.Contains("500", ex.Message);
        Assert.Contains("boom", ex.Message);
    }
}
