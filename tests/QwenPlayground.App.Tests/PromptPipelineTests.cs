using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Templates;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Tests;

/// <summary>
/// Конвейер промпта на подставном ICompletionSource — без HTTP. Проверяем контракт
/// фабрики источников (шов мульти-бэкенда) и кэш серверных фактов.
/// </summary>
public sealed class PromptPipelineTests
{
    private sealed class FakeSource : ICompletionSource
    {
        public TokenUsage? LastUsage => null;
        public int CountCalls { get; private set; }

        /// <summary>null имитирует «сервер не дал число».</summary>
        public int? NextCount { get; set; } = 777;

        public Task<CompletionResult> CompleteAsync(string prompt, GenerationOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CompletionResult("ok", new TokenUsage(1, 2)));

        public async IAsyncEnumerable<string> StreamAsync(
            string prompt, GenerationOptions options, IReadOnlyList<string>? multimodalData = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return "ok";
        }

        public Task<int?> CountTokensAsync(string text, CancellationToken cancellationToken = default)
        {
            CountCalls++;
            return Task.FromResult(NextCount);
        }

        public void Dispose()
        {
        }
    }

    private static PromptPipeline Create(FakeSource source, out ServerProps serverProps)
    {
        serverProps = new ServerProps();
        var log = new ChatLog();
        log.Add(new ChatMessage { Role = ChatRole.User, Content = "привет" });

        return new PromptPipeline(
            () => log,
            () => "You are a test assistant.",
            new ToolRegistry(),
            serverProps,
            messages => null,
            ct => Task.FromResult<MultimodalContext?>(null),
            endpoint => source);
    }

    [Fact]
    public async Task CountNextTokens_UsesInjectedSource_AndCachesServerFact()
    {
        var source = new FakeSource { NextCount = 4321 };
        var pipeline = Create(source, out var serverProps);

        // /props недоступен в тестах — FetchAsync глотает ошибку, подсчёт не зависит от props.
        var count = await pipeline.CountNextTokensAsync(CancellationToken.None);

        Assert.Equal(4321, count);
        Assert.Equal(1, source.CountCalls);
        Assert.Equal(4321, serverProps.LastPromptTokens); // факт кэшируется для state-блока/диагностики
    }

    [Fact]
    public async Task CountNextTokens_SourceWithoutNumber_Throws()
    {
        var pipeline = Create(new FakeSource { NextCount = null }, out _);

        // Сервер не дал точное число → ход не может начаться вслепую.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.CountNextTokensAsync(CancellationToken.None));
    }

    [Fact]
    public void RenderForPreview_ContainsHistory_InQwenMarkup()
    {
        var pipeline = Create(new FakeSource(), out _);

        var preview = pipeline.RenderForPreview();

        Assert.Contains("привет", preview);
        Assert.Contains("im_start", preview); // Qwen-разметка присутствует
    }
}
