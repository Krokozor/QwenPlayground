using System.Net;
using System.Net.Sockets;
using System.Text;
using QwenPlayground.Core.Inference;

namespace QwenPlayground.Core.Tests;

public sealed class LlmCompletionClientTests
{
    private sealed class FakeHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    [Fact]
    public async Task StreamAsync_ParsesSseChunks()
    {
        // Нативный /completion (legacy): чанки {content, tokens_predicted, tokens_evaluated, stop}.
        // Последний чанк — пустой content + stop:true (вместо [DONE]).
        const string sse = """
            data: {"content":"Hello","tokens_predicted":1,"tokens_evaluated":7}

            data: {"content":" world","tokens_predicted":2,"tokens_evaluated":7}

            data: {"content":"","tokens_predicted":3,"tokens_evaluated":7,"stop":true}

            """;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
        using var client = new LlmCompletionClient("http://localhost", new FakeHandler(response));

        var chunks = new List<string>();
        await foreach (var chunk in client.StreamAsync("prompt", new GenerationOptions()))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(new[] { "Hello", " world" }, chunks);
    }

    [Fact]
    public async Task CompleteAsync_ReturnsText()
    {
        // Нативный /completion (legacy): {content, tokens_evaluated, tokens_predicted}.
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"content":"Hi there","tokens_evaluated":10,"tokens_predicted":5}""", Encoding.UTF8, "application/json")
        };
        using var client = new LlmCompletionClient("http://localhost", new FakeHandler(response));

        var result = await client.CompleteAsync("prompt", new GenerationOptions());

        Assert.Equal("Hi there", result.Text);
        Assert.Equal(10, result.Usage?.PromptTokens);
        Assert.Equal(5, result.Usage?.CompletionTokens);
    }

    [Fact]
    [Trait("Category", "Live")] // требует живого llama.cpp на :5001; без него тихо пропускается
    public async Task LiveServer_StreamsCompletion()
    {
        if (!IsServerUp())
        {
            return;
        }

        using var client = new LlmCompletionClient("http://127.0.0.1:5001");
        const string prompt = "<|im_start|>user\nReply with exactly: pong<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";
        var options = new GenerationOptions { MaxTokens = 32, Temperature = 0.0 };

        var builder = new StringBuilder();
        await foreach (var chunk in client.StreamAsync(prompt, options))
        {
            builder.Append(chunk);
        }

        Assert.Contains("pong", builder.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsServerUp()
    {
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.Connect("127.0.0.1", 5001);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
