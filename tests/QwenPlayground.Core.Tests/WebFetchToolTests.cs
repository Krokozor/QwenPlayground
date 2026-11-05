using System.Net;
using System.Text;
using QwenPlayground.Core.Tools;
using QwenPlayground.Core.Tools.Builtins;

namespace QwenPlayground.Core.Tests;

public sealed class WebFetchToolTests
{
    private static readonly ToolContext Context = new(Path.GetTempPath());

    [Fact]
    public async Task DeclaredResponseOverDefaultBudget_IsRefusedWithExplicitRetry()
    {
        using var client = Client(TextResponse("01234567890"));
        var tool = new WebFetchTool(client, defaultMaxResponseBytes: 10)
        {
            Url = "http://example.test/data.txt",
        };

        var result = await tool.ExecuteAsync(Context, CancellationToken.None);

        Assert.Contains("declared 11 bytes > 10-byte limit", result);
        Assert.Contains("max_bytes=11", result);
        Assert.Contains("max_bytes=0", result);
        Assert.DoesNotContain("01234567890", result);
    }

    [Fact]
    public async Task ExplicitMaxBytes_AllowsLargerResponse()
    {
        using var client = Client(TextResponse("01234567890"));
        var tool = new WebFetchTool(client, defaultMaxResponseBytes: 10)
        {
            Url = "http://example.test/data.txt",
            MaxBytes = 11,
        };

        var result = await tool.ExecuteAsync(Context, CancellationToken.None);

        Assert.Contains("01234567890", result);
        Assert.DoesNotContain("response too large", result);
    }

    [Fact]
    public async Task ZeroMaxBytes_ExplicitlyRemovesSizeLimit()
    {
        using var client = Client(TextResponse("01234567890"));
        var tool = new WebFetchTool(client, defaultMaxResponseBytes: 10)
        {
            Url = "http://example.test/data.txt",
            MaxBytes = 0,
        };

        var result = await tool.ExecuteAsync(Context, CancellationToken.None);

        Assert.Contains("01234567890", result);
    }

    [Fact]
    public async Task UnknownLengthResponse_StopsAtBudgetWithoutPartialContent()
    {
        var response = TextResponse("01234567890");
        response.Content.Headers.ContentLength = null;
        using var client = Client(response);
        var tool = new WebFetchTool(client, defaultMaxResponseBytes: 10)
        {
            Url = "http://example.test/data.txt",
        };

        var result = await tool.ExecuteAsync(Context, CancellationToken.None);

        Assert.Contains("exceeded the 10-byte limit while streaming", result);
        Assert.Contains("total size unknown", result);
        Assert.Contains("No partial content was returned", result);
        Assert.DoesNotContain("01234567890", result);
    }

    [Fact]
    public async Task LargeOutput_IsNotTruncatedBeforeGlobalAutoCap()
    {
        var marker = "END-OF-DOCUMENT";
        var body = new string('x', 200_100) + marker;
        using var client = Client(TextResponse(body));
        var tool = new WebFetchTool(client)
        {
            Url = "http://example.test/large.txt",
        };

        var result = await tool.ExecuteAsync(Context, CancellationToken.None);

        Assert.EndsWith(marker, result);
        Assert.True(result.Length > 200_000);
        Assert.DoesNotContain("Content truncated", result);
    }

    [Fact]
    public async Task RelativeRedirects_AreResolvedAgainstCurrentUrl()
    {
        var handler = new StubHttpHandler(
            request => Redirect("/dir/next"),
            request => Redirect("page"),
            request => TextResponse("redirected"));
        using var client = new HttpClient(handler);
        var tool = new WebFetchTool(client)
        {
            Url = "http://example.test/start",
        };

        var result = await tool.ExecuteAsync(Context, CancellationToken.None);

        Assert.Contains("Fetched http://example.test/dir/page", result);
        Assert.Contains("redirected", result);
        Assert.Equal(
            ["http://example.test/start", "http://example.test/dir/next", "http://example.test/dir/page"],
            handler.Requests);
    }

    [Fact]
    public async Task NegativeMaxBytes_IsRejectedBeforeRequest()
    {
        var handler = new StubHttpHandler(request => TextResponse("unreachable"));
        using var client = new HttpClient(handler);
        var tool = new WebFetchTool(client)
        {
            Url = "http://example.test/data.txt",
            MaxBytes = -1,
        };

        var result = await tool.ExecuteAsync(Context, CancellationToken.None);

        Assert.Contains("max_bytes must be zero or a positive", result);
        Assert.Empty(handler.Requests);
    }

    private static HttpClient Client(HttpResponseMessage response) =>
        new(new StubHttpHandler(request => response));

    private static HttpResponseMessage TextResponse(string text) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(text, Encoding.UTF8, "text/plain"),
        };

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) },
        };

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public StubHttpHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        }

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (_responses.Count == 0)
                throw new InvalidOperationException("Unexpected HTTP request.");
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}
