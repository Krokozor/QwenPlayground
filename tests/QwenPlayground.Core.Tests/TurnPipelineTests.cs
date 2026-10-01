using System.Text.Json.Nodes;
using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Tools;
using Xunit;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// TurnPipeline: оркестрация хода без LLM (шов runLoop — скриптованные события).
/// Проверяется: бюджет-проверка до FSM, переходы FSM, доставка событий в sink,
/// итог (окно/отмена/ошибка), флаг continue, состав запроса (каталог, тулы main-сессии).
/// </summary>
public sealed class TurnPipelineTests : IDisposable
{
    private readonly string _root;
    private readonly ChatLog _log;
    private readonly ChatStateMachine _chatState;
    private readonly ToolRegistry _tools;
    private readonly SessionController _sessions;
    private readonly SystemPromptAssembler _assembler;
    private readonly MemorySurfacer _surfacer;
    private readonly List<string> _statuses = new();
    private readonly List<bool> _generating = new();
    private int _shutdownCalls;
    private int _effectiveSizeCalls;
    private Exception? _effectiveSizeException;
    private readonly List<AgentEvent> _scriptedEvents = new();
    private Exception? _loopException;
    // Шов для тестов: цикл ждёт эту задачу перед событиями (блокировка хода посреди).
    private Task? _loopGate;
    private AgentLoopRequest? _observedRequest;
    private readonly TurnPipeline _pipeline;
    private readonly string _projectRootBackup;
    private string? _lastSessionIdBackup;

    public TurnPipelineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "turnpipe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _log = new ChatLog();
        _chatState = new ChatStateMachine();
        _tools = new ToolRegistry(typeof(AgentTool).Assembly);
        _surfacer = new MemorySurfacer();
        var draft = new DraftKeeper(
            () => string.Empty,
            _ => { },
            () => _sessions!.CurrentId,
            new SessionDraftStore(_root),
            () => 30);
        _sessions = new SessionController(_log, draft, _surfacer, _root);
        _sessions.EnsureMain();
        _assembler = new SystemPromptAssembler(
            () => _sessions.CurrentId,
            () => _sessions.PromptKey,
            () => _sessions.DirectoryFor(_sessions.CurrentId),
            () => SessionRoots.Resolve(_sessions.Root, AppSettings.Get().ProjectRoot),
            new InjectedIdentity(),
            new ExternalToolsNote(),
            _tools);
        var maintenance = new ContextMaintenance(
            _log,
            _chatState,
            new CompactionPreview(),
            (prompt, key, announce, ct) => Task.FromResult<string?>(null),
            new MemoryLayerStore(),
            _surfacer,
            _ => Task.FromResult(0),
            async () =>
            {
                _effectiveSizeCalls++;
                await Task.Yield();
                if (_effectiveSizeException is not null)
                {
                    throw _effectiveSizeException;
                }
                return 100_000;
            },
            () => _sessions.CurrentId,
            new ContextBackupStore(_root),
            new ContextMaintenance.Ui(
                status => _statuses.Add(status),
                generating => _generating.Add(generating),
                () => { }));
        _pipeline = new TurnPipeline(
            _log,
            _chatState,
            _tools,
            new StateBlockBuilder(
                () => { },
                () => 1,
                () => 100_000,
                new ServerProps(),
                () => _log.ToList(),
                () => _surfacer.GetSurfacedForStateBlock(),
                []),
            maintenance,
            new ServerProps(),
            new TurnSessionView(
                () => _sessions.CurrentId,
                () => _sessions.DirectoryFor(_sessions.CurrentId),
                () => _sessions.SamplerKey,
                () => _sessions.PromptKey,
                () => _sessions.StateBlockKey,
                _sessions.SaveCurrent,
                () => null, // тесты без пиннинга слотов
                () => _sessions.Root,
                _sessions.SetRoot),
            _assembler,
            _surfacer,
            status => _statuses.Add(status),
            generating => _generating.Add(generating),
            () => _shutdownCalls++,
            FakeLoop);
        // Тест не зависит от реального workspace: проект-корень — временный каталог (agentic=true).
        var settings = AppSettings.Get();
        _projectRootBackup = settings.ProjectRoot;
        _lastSessionIdBackup = settings.LastSessionId;
        settings.ProjectRoot = _root;
        settings.LastSessionId = MainAgent.SessionId;
    }

    public void Dispose()
    {
        var settings = AppSettings.Get();
        settings.ProjectRoot = _projectRootBackup;
        settings.LastSessionId = _lastSessionIdBackup;
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async IAsyncEnumerable<AgentEvent> FakeLoop(AgentLoopRequest request)
    {
        _observedRequest = request;
        if (_loopGate is not null)
        {
            await _loopGate;
        }
        foreach (var e in _scriptedEvents)
        {
            yield return e;
        }
        if (_loopException is not null)
        {
            throw _loopException;
        }
        await Task.Yield();
    }

    [Fact]
    public async Task BudgetCheck_Failure_ReturnsBudgetFailed_SavesHistory_RollsBackToIdle()
    {
        _effectiveSizeException = new InvalidOperationException("сервер недоступен");

        var outcome = await _pipeline.RunTurnAsync(continueLastAssistant: false, _ => { });

        Assert.True(outcome.BudgetFailed);
        Assert.Null(outcome.Error);
        Assert.Contains(_statuses, s => s.Contains("бюджета"));
        // «Один вход» (фаза 1): FSM переведён на входе (до бюджет-чека) и откатился в Idle
        // при бюджет-фейле; флаг генерации поднимался и опустился.
        Assert.Equal(new[] { true, false }, _generating);
        Assert.Equal(ChatState.Idle, _chatState.Current);
        // История сохранена (user-сообщение не «висит» несохранённым).
        Assert.True(File.Exists(Path.Combine(_root, MainAgent.SessionId, "chat.json")));
    }

    [Fact]
    public async Task SecondEntry_WhileBusy_ReturnsBusy_Gracefully()
    {
        // «Один вход» (фаза 1): второй вызов во время хода (двойной клик «отправить»,
        // wake/heartbeat) не стартует второй ход — FSM занят, итог Busy, без исключения;
        // первый ход не затронут и доходит до конца.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loopGate = gate.Task;

        var first = _pipeline.RunTurnAsync(continueLastAssistant: false, _ => { });
        for (var i = 0; i < 200 && _observedRequest is null; i++)
        {
            await Task.Delay(10);
        }
        Assert.NotNull(_observedRequest); // первый ход вошёл в цикл (FSM уже Generating)
        Assert.Equal(ChatState.Generating, _chatState.Current);

        var second = await _pipeline.RunTurnAsync(continueLastAssistant: false, _ => { });

        Assert.True(second.Busy);
        Assert.Null(second.Error);
        Assert.Equal(ChatState.Generating, _chatState.Current); // второй вход FSM не тронул

        gate.SetResult(true);
        var outcome = await first;
        Assert.False(outcome.Busy);
        Assert.Null(outcome.Error);
        Assert.Equal(ChatState.Idle, _chatState.Current);
    }

    [Fact]
    public async Task Turn_Completes_DeliversEvents_GeneratingToggles_FsmIdle()
    {
        _scriptedEvents.Add(new TokenEvent("привет"));
        _scriptedEvents.Add(new AgentDoneEvent());

        var seen = new List<AgentEvent>();
        var outcome = await _pipeline.RunTurnAsync(continueLastAssistant: false, seen.Add);

        Assert.False(outcome.BudgetFailed);
        Assert.False(outcome.Canceled);
        Assert.Null(outcome.Error);
        Assert.Equal(2, seen.Count);
        Assert.IsType<TokenEvent>(seen[0]);
        Assert.IsType<AgentDoneEvent>(seen[1]);
        Assert.Equal(new[] { true, false }, _generating);
        Assert.Equal(ChatState.Idle, _chatState.Current);
        Assert.Equal(0, _shutdownCalls);
    }

    [Fact]
    public async Task Turn_Cancelled_OutcomeCanceled_FsmIdle()
    {
        _loopException = new OperationCanceledException();

        var outcome = await _pipeline.RunTurnAsync(continueLastAssistant: false, _ => { });

        Assert.True(outcome.Canceled);
        Assert.Null(outcome.Error);
        Assert.Equal(new[] { true, false }, _generating);
        Assert.Equal(ChatState.Idle, _chatState.Current);
    }

    [Fact]
    public async Task Turn_LoopThrows_OutcomeCarriesError()
    {
        _loopException = new InvalidOperationException("boom");

        var outcome = await _pipeline.RunTurnAsync(continueLastAssistant: false, _ => { });

        Assert.False(outcome.Canceled);
        Assert.NotNull(outcome.Error);
        Assert.Equal("boom", outcome.Error!.Message);
        Assert.Equal(new[] { true, false }, _generating);
        Assert.Equal(ChatState.Idle, _chatState.Current);
    }

    [Fact]
    public async Task Turn_OutcomeCarriesStats()
    {
        // P2: итог хода несёт статистики (итерации, тулы, токены) — данные для
        // саммари в events-лог и постмортема. Токены — из GenerationInfo (сервер).
        _scriptedEvents.Add(new AssistantMessageEvent(new ChatMessage {
            Role = ChatRole.Assistant,
            Content = "итерация 1",
            Generation = new GenerationInfo { Prompt = "p", PromptTokens = 100, CompletionTokens = 10 } }));
        _scriptedEvents.Add(new ToolCallStartedEvent("read_file", new JsonObject()));
        _scriptedEvents.Add(new AssistantMessageEvent(new ChatMessage {
            Role = ChatRole.Assistant,
            Content = "итерация 2",
            Generation = new GenerationInfo { Prompt = "p", PromptTokens = 200, CompletionTokens = 20 } }));
        _scriptedEvents.Add(new AgentDoneEvent());

        var outcome = await _pipeline.RunTurnAsync(continueLastAssistant: false, _ => { });

        Assert.Equal(2, outcome.Iterations);
        Assert.Equal(1, outcome.ToolCalls);
        Assert.Equal(300, outcome.PromptTokens);
        Assert.Equal(30, outcome.CompletionTokens);
        Assert.False(outcome.Compacted);
        Assert.Null(outcome.SlotId); // тесты без пиннинга слотов
        Assert.True(outcome.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Turn_CompactingTransition_SetsCompactedFlag()
    {
        // P2: флаг Compacted — из переходов FSM (Generating → Compacting → Generating,
        // как делает цикл при сжатии контекста между итерациями).
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loopGate = gate.Task;
        _scriptedEvents.Add(new AgentDoneEvent());

        var turn = _pipeline.RunTurnAsync(continueLastAssistant: false, _ => { });
        for (var i = 0; i < 200 && _observedRequest is null; i++)
        {
            await Task.Delay(10);
        }
        Assert.NotNull(_observedRequest); // ход вошёл в цикл (FSM — Generating)
        _chatState.Transition(ChatState.Compacting);
        _chatState.Transition(ChatState.Generating);
        gate.SetResult(true);

        var outcome = await turn;

        Assert.True(outcome.Compacted);
        Assert.False(outcome.Canceled);
        Assert.Null(outcome.Error);
        Assert.Equal(ChatState.Idle, _chatState.Current);
    }

    [Fact]
    public async Task ContinueTurn_SkipsBudgetCheck_PassesContinueFlag()
    {
        _log.Add(ChatMessage.User("вопрос"));
        _log.Add(ChatMessage.Assistant("ответ"));

        var outcome = await _pipeline.RunTurnAsync(continueLastAssistant: true, _ => { });

        Assert.False(outcome.BudgetFailed);
        Assert.Equal(0, _effectiveSizeCalls); // бюджет-проверка — только для нового хода
        Assert.NotNull(_observedRequest);
        Assert.True(_observedRequest!.ContinueLastAssistant);
    }

    [Fact]
    public async Task NewTurn_ChecksBudget_PassesRequestWithSessionDirAndConversation()
    {
        _log.Add(ChatMessage.User("вопрос"));

        await _pipeline.RunTurnAsync(continueLastAssistant: false, _ => { });

        Assert.Equal(1, _effectiveSizeCalls);
        Assert.NotNull(_observedRequest);
        var request = _observedRequest!;
        Assert.False(request.ContinueLastAssistant);
        Assert.Same(_log, request.Conversation);
        Assert.Equal(_sessions.DirectoryFor(_sessions.CurrentId), request.SessionDir);
        Assert.NotNull(request.ContextBudgetGuard);
        Assert.NotNull(request.SystemPromptProvider);
        Assert.NotNull(request.StateProvider); // main-сессия: state-блок включён
        Assert.NotNull(request.Generation);
    }

    [Fact]
    public async Task MainSession_WithProjectRoot_ToolsAllowed()
    {
        await _pipeline.RunTurnAsync(continueLastAssistant: false, _ => { });

        Assert.NotNull(_observedRequest);
        var request = _observedRequest!;
        Assert.True(request.AllowToolExecution); // main-сессия: профиль разрешает инструменты
        Assert.NotNull(request.ToolDefinitions);
        Assert.NotEmpty(request.ToolDefinitions);
        // Рабочая папка (план 2026-10-01): live-провайдер сессии + колбэк смены.
        // У сессии своего root'а нет — провайдер отдаёт null (фоллбек в настройки
        // делает AgentLoop на итерации), колбэк смены доступен.
        Assert.NotNull(request.SessionRoot);
        Assert.NotNull(request.SetSessionRoot);
        Assert.Null(request.SessionRoot());
    }

    [Fact]
    public async Task MainSession_WithoutProjectRoot_ToolsStillAllowed()
    {
        // Гейт агентности по ProjectRoot убран (2026-10-01): любой чат инструментален,
        // набор определяет профиль. Пустой root не отключает инструменты.
        AppSettings.Get().ProjectRoot = string.Empty;

        await _pipeline.RunTurnAsync(continueLastAssistant: false, _ => { });

        Assert.NotNull(_observedRequest);
        var request = _observedRequest!;
        Assert.True(request.AllowToolExecution);
        Assert.NotNull(request.ToolDefinitions);
        Assert.NotEmpty(request.ToolDefinitions);
    }

    [Fact]
    public void ActiveToken_NoneWhileIdle()
    {
        Assert.Equal(CancellationToken.None, _pipeline.ActiveToken);
    }
}
