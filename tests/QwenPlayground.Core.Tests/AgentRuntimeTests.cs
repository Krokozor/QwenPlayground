using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Runtime;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Скоуп агента (шаг к оркестратору): изоляция профиля настроек, маршрутизация
/// интерактива через данные (ToolContext), фасад AgentInteraction над main-скоупом,
/// чтение циклом конфига из профиля скоупа.
/// </summary>
public sealed class AgentRuntimeTests
{
    [Fact]
    public void Main_Settings_ResolvesProcessSingleton()
    {
        Assert.Same(AppSettings.Get(), AgentRuntime.Main.Settings);
    }

    [Fact]
    public void Scopes_IsolateSettingsProfiles()
    {
        var child = new AppSettings { Endpoint = "http://child:9" };
        var scope = new AgentRuntime { SettingsProvider = () => child };

        Assert.Equal("http://child:9", scope.Settings.Endpoint);
        Assert.Same(child, scope.Settings);
        // main-скоуп не задет изоляцией
        Assert.NotSame(child, AgentRuntime.Main.Settings);
    }

    [Fact]
    public void TryConfirm_NoProvider_ReturnsNull()
    {
        var scope = new AgentRuntime();

        Assert.Null(scope.TryConfirm("q?", CancellationToken.None));
    }

    [Fact]
    public void AgentInteraction_Facade_ReadsAndWritesMainScope()
    {
        try
        {
            Func<string, CancellationToken, Task<bool>> provider = (_, _) => Task.FromResult(true);
            AgentInteraction.Confirm = provider;

            Assert.Same(provider, AgentRuntime.Main.Confirm);
            Assert.Same(AgentRuntime.Main.Confirm, AgentInteraction.Confirm);
        }
        finally
        {
            AgentInteraction.Confirm = null; // не протекаем в параллельные тесты
        }
    }

    [Fact]
    public void ToolContext_Scope_FallsBackToMain()
    {
        var context = new ToolContext(@"C:\tmp");

        Assert.Same(AgentRuntime.Main, context.Scope);
        Assert.Null(context.Runtime);
    }

    [Fact]
    public void ToolContext_Scope_PrefersInjectedRuntime()
    {
        var scope = new AgentRuntime();
        var context = new ToolContext(@"C:\tmp", runtime: scope);

        Assert.Same(scope, context.Scope);
    }

    [Fact]
    public async Task AgentLoop_ReadsConfigFromRuntimeProfile()
    {
        string? usedEndpoint = null;
        var childSettings = new AppSettings
        {
            Endpoint = "http://child-agent:9",
            ProjectRoot = @"C:\tmp",
            MaxIterations = 1
        };
        var source = new OneChunkSource();
        var log = new ChatLog();
        log.Add(new ChatMessage { Role = ChatRole.User, Content = "привет" });

        var loop = new AgentLoop(new ToolRegistry());
        await foreach (var _ in loop.RunAsync(new AgentLoopRequest
        {
            Conversation = log,
            AllowToolExecution = false,
            Runtime = new AgentRuntime { SettingsProvider = () => childSettings },
            CompletionSourceFactory = endpoint => { usedEndpoint = endpoint; return source; }
        }))
        {
            // События цикла здесь не важны: проверяем только профиль.
        }

        Assert.Equal("http://child-agent:9", usedEndpoint);
    }

    /// <summary>Заглушка источника: один чанк и завершение — ход заканчивается сразу.</summary>
    private sealed class OneChunkSource : ICompletionSource
    {
        public TokenUsage? LastUsage => null;

        public Task<CompletionResult> CompleteAsync(string prompt, GenerationOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CompletionResult("ok", null));

        public async IAsyncEnumerable<string> StreamAsync(
            string prompt, GenerationOptions options, IReadOnlyList<string>? multimodalData = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return "ok";
        }

        public Task<int?> CountTokensAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(null);

        public void Dispose()
        {
        }
    }
}
