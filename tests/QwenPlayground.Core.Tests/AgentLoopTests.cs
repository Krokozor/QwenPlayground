using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Templates;
using QwenPlayground.Core.Tools;
using Xunit;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// AgentLoop — ядро цикла (рендер → стрим → парсинг → tool_calls → повтор) — без LLM:
/// скриптованный <see cref="ICompletionSource"/> (фабрика CompletionSourceFactory — шов,
/// заложенный как «точка роста для тестовых заглушек»). Инструменты — поддельный
/// ToolExecutor (реестр не используется). Покрывает: базовый тул-цикл, лимит итераций,
/// отмену посреди стрима, fallback токенов на /tokenize, nag за отсутствие тулов,
/// single-чат (тулы не исполняются), жизненный цикл клиента (Dispose).
/// </summary>
public sealed class AgentLoopTests : IDisposable
{
    private readonly ChatLog _log = new();
    private readonly List<AgentEvent> _events = new();
    private readonly List<(string Name, JsonObject Args)> _executedTools = new();
    private readonly AgentLoop _loop;
    private readonly bool _trafficLogEnabledBackup;

    public AgentLoopTests()
    {
        _loop = new AgentLoop(new ToolRegistry(typeof(AgentTool).Assembly));
        // Цикл пишет каждый промпт/ответ в TrafficLog — тест не должен раздувать реальный лог.
        _trafficLogEnabledBackup = AppSettings.Get().TrafficLogEnabled;
        AppSettings.Get().TrafficLogEnabled = false;
    }

    public void Dispose()
    {
        AppSettings.Get().TrafficLogEnabled = _trafficLogEnabledBackup;
    }

    // ── Скриптованный источник завершений ────────────────────────────────────────────

    /// <summary>
    /// Очередь «ответов модели»: каждый вызов StreamAsync выдаёт следующий ответ
    /// двумя чанками (проверка накопления raw в цикле) и ставит LastUsage ответа.
    /// </summary>
    private sealed class ScriptedCompletionSource : ICompletionSource
    {
        private readonly Queue<string> _responses;
        private readonly Queue<TokenUsage?> _usages;
        private readonly int _countTokens;

        public int StreamCalls;
        public int CountTokensCalls;
        public int Disposed;
        /// <summary>
        /// true — второй чанк выдаётся только после отмены (Task.Delay(∞, ct)):
        /// детерминированная отмена ПОСРЕДИ стрима для Cancellation-теста.
        /// </summary>
        public bool GateSecondChunk;

        public ScriptedCompletionSource(int countTokens, params (string Raw, TokenUsage? Usage)[] responses)
        {
            _countTokens = countTokens;
            _responses = new Queue<string>(responses.Select(r => r.Raw));
            _usages = new Queue<TokenUsage?>(responses.Select(r => r.Usage));
        }

        public TokenUsage? LastUsage { get; private set; }

        public Task<CompletionResult> CompleteAsync(string prompt, GenerationOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("AgentLoop использует только StreamAsync");

        public async IAsyncEnumerable<string> StreamAsync(
            string prompt,
            GenerationOptions options,
            IReadOnlyList<string>? multimodalData = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("scripted source: ответов больше нет");
            }
            var raw = _responses.Dequeue();
            LastUsage = _usages.Dequeue();
            var half = raw.Length / 2;
            yield return raw[..half];
            if (GateSecondChunk)
            {
                // Зависаем на втором чанке, пока тест не отменит (OCE из Task.Delay).
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            else
            {
                await Task.Yield();
            }
            cancellationToken.ThrowIfCancellationRequested();
            yield return raw[half..];
        }

        public Task<int?> CountTokensAsync(string text, CancellationToken cancellationToken = default)
        {
            CountTokensCalls++;
            return Task.FromResult<int?>(_countTokens);
        }

        public void Dispose() => Disposed++;
    }

    // ── Сборка «сырых» ответов модели (разметка — из QwenSpecialTokens) ─────────────

    private static string FinalResponse(string thinking, string content)
    {
        var sb = new StringBuilder();
        sb.Append(thinking).Append('\n');
        sb.Append(QwenSpecialTokens.ThinkEnd).Append('\n');
        sb.Append(content);
        return sb.ToString();
    }

    private static string ToolCallResponse(string thinking, string toolName, params (string Name, string Value)[] args)
    {
        var sb = new StringBuilder();
        sb.Append(thinking).Append('\n');
        sb.Append(QwenSpecialTokens.ThinkEnd).Append('\n');
        sb.Append(QwenSpecialTokens.ToolCallStart).Append('\n');
        sb.Append(QwenSpecialTokens.FunctionStart(toolName)).Append('\n');
        foreach (var (name, value) in args)
        {
            sb.Append(QwenSpecialTokens.ParameterStart(name)).Append('\n').Append(value).Append('\n');
            sb.Append(QwenSpecialTokens.ParameterEnd).Append('\n');
        }
        sb.Append(QwenSpecialTokens.FunctionEnd).Append('\n');
        sb.Append(QwenSpecialTokens.ToolCallEnd);
        return sb.ToString();
    }

    // ── Прогон цикла ─────────────────────────────────────────────────────────────────

    private async Task<ScriptedCompletionSource> RunLoopAsync(
        Func<AgentLoopRequest, AgentLoopRequest>? modify = null,
        CancellationToken cancellationToken = default,
        Action<ScriptedCompletionSource>? configureSource = null,
        (string Raw, TokenUsage? Usage)[]? responses = null)
    {
        responses ??= [];
        var source = new ScriptedCompletionSource(countTokens: 42, responses);
        configureSource?.Invoke(source);
        // Реальный цикл рендерит промпт: разговор не может быть пустым.
        if (_log.Count == 0)
        {
            _log.Add(ChatMessage.User("вопрос"));
        }
        var request = new AgentLoopRequest
        {
            Conversation = _log,
            CompletionSourceFactory = _ => source,
            AllowToolExecution = true,
            ToolDefinitions = Array.Empty<ToolDefinition>(),
            ToolExecutor = (name, args, ctx, ct) =>
            {
                _executedTools.Add((name, args));
                return Task.FromResult(new ToolExecutionResult($"result of {name}", null));
            },
            MaxIterations = 10,
            SanityCheckInterval = 0,
            ReasoningEffort = ReasoningEffort.Medium,
            Generation = new GenerationOptions(),
            CancellationToken = cancellationToken
        };
        request = modify?.Invoke(request) ?? request;
        await foreach (var e in _loop.RunAsync(request))
        {
            _events.Add(e);
        }
        return source;
    }

    // ── Сценарии ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ToolLoop_ExecutesTool_Continues_FinishesOnFinalAnswer()
    {
        var source = await RunLoopAsync(responses: new[]
        {
            (ToolCallResponse("думаю, надо прочитать файл", "read_file", ("path", "a.txt")), new TokenUsage(100, 10)),
            (FinalResponse("прочитал", "Готово, файл прочитан."), new TokenUsage(200, 20))
        });

        // События: 2×(чанки + ассистент) + тул-пара + done.
        var assistants = _events.OfType<AssistantMessageEvent>().ToList();
        Assert.Equal(2, assistants.Count);
        Assert.Equal("Готово, файл прочитан.", assistants[1].Message.Content);
        Assert.Equal("думаю, надо прочитать файл", assistants[0].Message.Reasoning);
        Assert.NotNull(assistants[0].Message.ToolCalls);
        Assert.Equal("read_file", assistants[0].Message.ToolCalls![0].Name);

        var started = Assert.IsType<ToolCallStartedEvent>(
            _events.Single(e => e is ToolCallStartedEvent));
        Assert.Equal("read_file", started.Name);
        Assert.Equal("a.txt", (string)started.Arguments["path"]!);
        Assert.Contains(_events, e => e is ToolCallFinishedEvent);
        Assert.IsType<AgentDoneEvent>(_events[^1]);

        // Исполнитель вызван один раз с распарсенными аргументами.
        Assert.Single(_executedTools);
        Assert.Equal("read_file", _executedTools[0].Name);
        Assert.Equal("a.txt", (string)_executedTools[0].Args["path"]!);

        // Разговор: user → assistant(тул-колл) → tool(результат) → assistant(финал).
        Assert.Equal(4, _log.Count);
        Assert.Equal(ChatRole.User, _log[0].Role);
        Assert.Equal(ChatRole.Assistant, _log[1].Role);
        Assert.Equal(ChatRole.Tool, _log[2].Role);
        Assert.Equal("result of read_file", _log[2].Content);
        Assert.Equal(ChatRole.Assistant, _log[3].Role);

        // Токены — из LastUsage сервера (usage первой итерации): /tokenize не запрашивался.
        Assert.Equal(100, assistants[0].Message.Generation!.PromptTokens);
        Assert.Equal(10, assistants[0].Message.Generation.CompletionTokens);
        Assert.Equal(200, assistants[1].Message.Generation!.PromptTokens);
        Assert.Equal(20, assistants[1].Message.Generation.CompletionTokens);
        Assert.Equal(0, source.CountTokensCalls);

        // Клиент создан фабрикой и закрыт циклом (using).
        Assert.Equal(2, source.StreamCalls);
        Assert.Equal(1, source.Disposed);
    }

    [Fact]
    public async Task IterationLimit_Exhausted_EmitsAgentError_NoDone()
    {
        await RunLoopAsync(
            modify: r => r with { MaxIterations = 2 },
            responses: new[]
            {
                (ToolCallResponse("итерация 1", "shell", ("command", "echo 1")), new TokenUsage(10, 1)),
                (ToolCallResponse("итерация 2", "shell", ("command", "echo 2")), new TokenUsage(20, 2))
            });

        Assert.Equal(2, _executedTools.Count);
        Assert.DoesNotContain(_events, e => e is AgentDoneEvent);
        var error = Assert.IsType<AgentErrorEvent>(_events[^1]);
        Assert.Equal("reached iteration limit (2)", error.Message);
    }

    [Fact]
    public async Task Cancellation_MidStream_ThrowsOperationCanceled()
    {
        // Детерминированность: фейк зависает на втором чанке (GateSecondChunk),
        // отмена гарантированно приходит ПОСРЕДИ стрима, а не после его завершения.
        var cts = new CancellationTokenSource();
        var run = RunLoopAsync(
            cancellationToken: cts.Token,
            configureSource: s => s.GateSecondChunk = true,
            responses: new[]
            {
                (FinalResponse("долгая мысль", "долгий ответ"), new TokenUsage(10, 1))
            });

        // Дождаться первого чанка (цикл вошёл в стрим) ИЛИ падения run (страховка от
        // вечного спина: если цикл умер до первого события, ждём его исключения, а не событий).
        while (_events.Count == 0 && !run.IsCompleted)
        {
            await Task.Delay(5);
        }
        cts.Cancel();

        // ThrowsAny: из Task.Delay(∞, ct) прилетает TaskCanceledException (наследник OCE).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
    }

    [Fact]
    public async Task NoUsage_FallsBackToCountTokens()
    {
        var source = await RunLoopAsync(responses: new (string Raw, TokenUsage? Usage)[]
        {
            (FinalResponse("мысль", "ответ"), null)
        });

        var assistant = Assert.Single(_events.OfType<AssistantMessageEvent>());
        // Сервер не отдал usage — цикл запросил точное число у /tokenize (фейк отдаёт 42).
        Assert.Equal(42, assistant.Message.Generation!.PromptTokens);
        Assert.Null(assistant.Message.Generation.CompletionTokens);
        Assert.Equal(1, source.CountTokensCalls);
    }

    [Fact]
    public async Task Nag_OnNoToolCall_Repeats_UntilMaxNags_ThenDone()
    {
        await RunLoopAsync(
            modify: r => r with { NagOnNoToolCall = true, NagText = "KEEP GOING", MaxNags = 1 },
            responses: new[]
            {
                (FinalResponse("первый ответ без тулов", "первый"), new TokenUsage(10, 1)),
                (FinalResponse("второй ответ без тулов", "второй"), new TokenUsage(20, 2))
            });

        // Первый ответ без тулов → nag (1/1) и продолжение; второй → лимит nag'ов, done.
        var nags = _events.OfType<NagEvent>().ToList();
        Assert.Single(nags);
        Assert.Equal("KEEP GOING", nags[0].Text);
        Assert.IsType<AgentDoneEvent>(_events[^1]);
        // Nag-сообщение (user) попало в разговор — модель видит его на следующем рендере.
        Assert.Contains(_log, m => m.Role == ChatRole.User && m.Content == "KEEP GOING");
    }

    [Fact]
    public async Task NoToolExecution_ToolCallMarkup_NotExecuted_DoneAfterFirstResponse()
    {
        // Single-чат: инструменты модели не рекламировались, разметка tool_call в выводе —
        // шум; цикл завершается после первого ответа, ничего не исполняя.
        await RunLoopAsync(
            modify: r => r with { AllowToolExecution = false },
            responses: new[]
            {
                (ToolCallResponse("хочу вызвать тул", "read_file", ("path", "a.txt")), new TokenUsage(10, 1))
            });

        Assert.Empty(_executedTools);
        Assert.IsType<AgentDoneEvent>(_events[^1]);
        // Сообщение с разметкой осталось в истории (как есть): user + assistant(разметка).
        Assert.Equal(2, _log.Count);
        var assistant = _log[1];
        Assert.NotNull(assistant.ToolCalls);
        Assert.Equal("read_file", assistant.ToolCalls![0].Name);
    }
}
