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
    private AgentLoopRequest? _observedRequest;
    private readonly TurnPipeline _pipeline;
    private readonly string _projectRootBackup;
    private readonly string _lastSessionIdBackup;

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
            () => _sessions.CurrentId,
            new SessionDraftStore(_root),
            () => 30);
        _sessions = new SessionController(_log, draft, _surfacer, _root);
        _sessions.EnsureMain();
        _assembler = new SystemPromptAssembler(
            () => _sessions.CurrentId,
            () => _sessions.PromptKey,
            () => _sessions.DirectoryFor(_sessions.CurrentId),
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
            _sessions,
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
    public async Task BudgetCheck_Failure_ReturnsBudgetFailed_SavesHistory_NoFsm()
    {
        _effectiveSizeException = new InvalidOperationException("сервер недоступен");

        var outcome = await _pipeline.RunTurnAsync(continueLastAssistant: false, _ => { });

        Assert.True(outcome.BudgetFailed);
        Assert.Null(outcome.Error);
        Assert.Contains(_statuses, s => s.Contains("бюджета"));
        Assert.Empty(_generating); // FSM не переводился, флаг генерации не поднимался
        Assert.Equal(ChatState.Idle, _chatState.Current);
        // История сохранена (user-сообщение не «висит» несохранённым).
        Assert.True(File.Exists(Path.Combine(_root, MainAgent.SessionId, "chat.json")));
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
        Assert.True(request.AllowToolExecution); // agentic (ProjectRoot задан) && main-сессия
        Assert.NotEmpty(request.ToolDefinitions);
    }

    [Fact]
    public void ActiveToken_NoneWhileIdle()
    {
        Assert.Equal(CancellationToken.None, _pipeline.ActiveToken);
    }
}
