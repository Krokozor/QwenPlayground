using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Runtime;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Templates;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Agent;

/// <summary>
/// Оркестрация хода (домен, вынесен из MainViewModel): бюджет-проверка перед ходом,
/// переходы FSM, запуск AgentLoop (профили, инструменты, state-блок, бюджет-гард),
/// отмена, рестарт в новую сборку.
///
/// Видом не владеет: VM потребляет ход как sink событий (Action&lt;AgentEvent&gt; —
/// пузыри в ObservableCollection) и получает итог (TurnOutcome), сам решая, куда
/// показать ошибку. Реакции UI (статус, флаг генерации, shutdown процесса) —
/// делегаты: Core не знает про WPF.
/// </summary>
public sealed class TurnPipeline
{
    private readonly ChatLog _log;
    private readonly ChatStateMachine _chatState;
    private readonly ToolRegistry _toolRegistry;
    private readonly StateBlockBuilder _stateBlocks;
    private readonly ContextMaintenance _maintenance;
    private readonly ServerProps _serverProps;
    private readonly SessionController _sessions;
    private readonly SystemPromptAssembler _promptAssembler;
    private readonly MemorySurfacer _memorySurfacer;
    private readonly Action<string> _onStatus;
    private readonly Action<bool> _onGeneratingChanged;
    private readonly Action _shutdownApp;
    // Шов для тестов: по умолчанию — реальный цикл (AgentLoop строит LLM-клиент сам).
    private readonly Func<AgentLoopRequest, IAsyncEnumerable<AgentEvent>> _runLoop;

    private CancellationTokenSource? _cancellation;

    public TurnPipeline(
        ChatLog log,
        ChatStateMachine chatState,
        ToolRegistry toolRegistry,
        StateBlockBuilder stateBlocks,
        ContextMaintenance maintenance,
        ServerProps serverProps,
        SessionController sessions,
        SystemPromptAssembler promptAssembler,
        MemorySurfacer memorySurfacer,
        Action<string> onStatus,
        Action<bool> onGeneratingChanged,
        Action shutdownApp,
        Func<AgentLoopRequest, IAsyncEnumerable<AgentEvent>>? runLoop = null)
    {
        _log = log;
        _chatState = chatState;
        _toolRegistry = toolRegistry;
        _stateBlocks = stateBlocks;
        _maintenance = maintenance;
        _serverProps = serverProps;
        _sessions = sessions;
        _promptAssembler = promptAssembler;
        _memorySurfacer = memorySurfacer;
        _onStatus = onStatus;
        _onGeneratingChanged = onGeneratingChanged;
        _shutdownApp = shutdownApp;
        _runLoop = runLoop ?? (request => new AgentLoop(_toolRegistry).RunAsync(request));
    }

    /// <summary>Токен активного хода (None, пока хода нет) — для live-реколла и фоновых проб.</summary>
    public CancellationToken ActiveToken => _cancellation?.Token ?? CancellationToken.None;

    /// <summary>Отменить активный ход.</summary>
    public void Cancel() => _cancellation?.Cancel();

    /// <summary>
    /// Провести ход: бюджет-проверка (кроме continue) → FSM → AgentLoop → итог.
    /// onEvent — sink вида для событий цикла (пузыри); доменная логика — здесь.
    /// </summary>
    public async Task<TurnOutcome> RunTurnAsync(bool continueLastAssistant, Action<AgentEvent> onEvent)
    {
        // Бюджет-проверка идёт ДО try/catch и до перевода FSM: падение здесь
        // (сервер недоступен, /tokenize не вернул точное число) раньше оставляло ход в тишине —
        // fire-and-forget задача (heartbeat/wake) гасла без следа, а добавленное user-сообщение
        // «висело» несохранённым. Показываем ошибку и сохраняем историю.
        if (!continueLastAssistant)
        {
            try
            {
                await _maintenance.EnsureBudgetAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _onStatus($"ошибка проверки бюджета контекста: {exception.Message}");
                _sessions.SaveCurrent();
                return new TurnOutcome { BudgetFailed = true };
            }
        }
        // Режим всегда агентный (тумблер режимов убран из UI, 2026-08-22): инструменты
        // доступны, если задан проект.
        var settings = AppSettings.Get();
        var agentic = settings.ProjectRoot.Trim().Length > 0;
        if (agentic)
        {
            Directory.CreateDirectory(settings.ProjectRoot);
        }
        return await RunCoreAsync(agentic, continueLastAssistant, onEvent);
    }

    /// <summary>
    /// Реальный ход: FSM, AgentLoop, обработка отмены/ошибки, рестарт. Единый путь
    /// генерации (всегда агентный: тумблер режимов убран 2026-08-22);
    /// allowToolExecution/toolDefinitions зависят от того, задан ли ProjectRoot:
    /// без проекта ход идёт как обычный чат — инструменты не рекламируются и не выполняются.
    /// </summary>
    private async Task<TurnOutcome> RunCoreAsync(bool agentic, bool continueLastAssistant, Action<AgentEvent> onEvent)
    {
        var continued = continueLastAssistant && _log.Count > 0 &&
            _log[^1].Role == ChatRole.Assistant
            ? _log[^1]
            : null;
        // FSM: Idle → Generating
        _chatState.Transition(ChatState.Generating);
        _onGeneratingChanged(true);
        _cancellation = new CancellationTokenSource();
        TurnOutcome outcome;
        try
        {
            var sessionDir = _sessions.DirectoryFor(_sessions.CurrentId);
            var settings = AppSettings.Get();
            var multimodal = await MultimodalContext.BuildAsync(sessionDir, settings.Endpoint, _serverProps, _cancellation.Token);
            // Профиль чата: три независимых куска из статичного хранилища (default = как раньше).
            // main-агент ведётся идентичностью — промпт-кусок и отключение state-блока на него не действуют.
            var isMain = _sessions.CurrentId == MainAgent.SessionId;
            var profiles = ChatProfiles.Get();
            var sampler = profiles.ResolveSampler(_sessions.SamplerKey);
            var prompt = profiles.ResolvePrompt(_sessions.PromptKey);
            var stateEnabled = isMain || profiles.ResolveStateBlock(_sessions.StateBlockKey).Enabled;
            var toolsAllowed = agentic && (isMain || prompt.Tools);
            await foreach (var agentEvent in _runLoop(new AgentLoopRequest {
                Conversation = _log,
                OnFactSaved = item => _memorySurfacer.SurfaceOwnWrite(item.Id, item.Content),
                ContinueLastAssistant = continued is not null,
                AllowToolExecution = toolsAllowed,
                ToolDefinitions = toolsAllowed ? _promptAssembler.ToolsFor(prompt.AllowedTools) : Array.Empty<ToolDefinition>(),
                Generation = settings.ToGenerationOptions(sampler),
                MaxIterations = settings.ResolveMaxIterations(sampler),
                // Nag самопроверки живёт ВНУТРИ state-блока — без блока nag'ать некуда.
                SanityCheckInterval = stateEnabled ? settings.ResolveSanityCheckInterval(sampler) : 0,
                ReasoningEffort = ParseEffort(prompt.ReasoningEffort),
                StateProvider = stateEnabled ? messages => _stateBlocks.Build() : null,
                SystemPromptProvider = _ => _promptAssembler.ResolveSystemPrompt(),
                ToolExecutor = async (name, args, ctx, ct) => {
                    // Менеджмент памяти сбрасывает mem_nag: модель задела memory_* — значит занималась.
                    if (name.StartsWith("memory_", StringComparison.Ordinal))
                    {
                        _memorySurfacer.OnMemoryToolUsed();
                    }
                    return await _toolRegistry.ExecuteDetailedAsync(name, args, ctx, ct);
                },
                // FSM: Generating → Compacting → Generating (между итерациями). Точный размер
                // промпта — у сервера (/tokenize); решение «сжимать» и само сжатие — в ContextMaintenance.
                ContextBudgetGuard = ct => _maintenance.EnsureBudgetAsync(ct),
                Multimodal = multimodal,
                SessionDir = sessionDir,
                CancellationToken = _cancellation.Token
            }))
            {
                onEvent(agentEvent);
            }
            outcome = new TurnOutcome { Agentic = agentic };
        }
        catch (OperationCanceledException)
        {
            outcome = new TurnOutcome { Agentic = agentic, Canceled = true };
        }
        catch (Exception exception)
        {
            // Куда показать ошибку (пузырь или статус) — решает вид: получает исключение в итоге.
            outcome = new TurnOutcome { Agentic = agentic, Error = exception };
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            // FSM: Generating → Idle (если ещё не в RestartPending).
            // Сначала FSM, потом IsGenerating=false: уведомление CanExecuteChanged должно
            // стрельнуть, когда IsBusy уже false, иначе кнопка отката останется серой.
            if (_chatState.Current == ChatState.Generating)
            {
                _chatState.Transition(ChatState.Idle);
            }
            _onGeneratingChanged(false);
        }
        if (agentic && SelfBuildService.ConsumeRestartRequest() is { } restartBuildId)
        {
            RestartInto(restartBuildId);
        }
        return outcome;
    }

    /// <summary>
    /// Рестарт в новую сборку: сохранить историю, запустить launcher (pointer-режим),
    /// закрыть процесс (делегат UI — Core не знает про WPF).
    /// </summary>
    private void RestartInto(string buildId)
    {
        _sessions.SaveCurrent();
        // Launcher в pointer-режиме (pid + buildId): current.txt = buildId, старт из run/<id>.
        // Старые версии приложения передают только pid — Launcher тогда работает в legacy-режиме.
        var launcher = Path.Combine(SelfBuildPaths.LauncherDir, "QwenPlayground.Launcher.exe");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
            FileName = launcher,
            Arguments = $"{Environment.ProcessId} {buildId}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        _shutdownApp();
    }

    /// <summary>Усилие размышления из профиля («XHigh»/«Medium»/«Low»); пустое/мусорное — из настроек.</summary>
    private static ReasoningEffort? ParseEffort(string text) =>
        !string.IsNullOrWhiteSpace(text) && Enum.TryParse<ReasoningEffort>(text.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : null;
}

/// <summary>Итог хода: вид решает, куда показать ошибку (пузырь ответа или статус-строка).</summary>
public sealed class TurnOutcome
{
    /// <summary>Ход отменён (Cancel).</summary>
    public bool Canceled { get; init; }
    /// <summary>Ход не стартовал: не прошла проверка бюджета контекста (статус и сейв уже сделаны).</summary>
    public bool BudgetFailed { get; init; }
    /// <summary>Ход упал (цикл бросил).</summary>
    public Exception? Error { get; init; }
    /// <summary>Ход был агентным (задан проект) — для выбора показа ошибки.</summary>
    public bool Agentic { get; init; }
}
