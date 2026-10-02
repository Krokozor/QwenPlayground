using System.Diagnostics;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Runtime;
using QwenPlayground.Core.SelfBuild;
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
    private readonly TurnSessionView _session;
    private readonly SystemPromptAssembler _promptAssembler;
    private readonly MemorySurfacer _memorySurfacer;
    private readonly Action<string> _onStatus;
    private readonly Action<bool> _onGeneratingChanged;
    private readonly Action _shutdownApp;
    // Напоминатель TODO: OnRendered — только после реального рендера (как MemorySurfacer).
    private readonly TodoReminder? _todo;
    // Скоуп агента рантайма (фаза 2, план 2026-09-28): ход идёт в скоупе СВОЕГО рантайма
    // (main — AgentRuntime.Main, pinned — собственный), интерактив — через ToolContext.Scope.
    private readonly AgentRuntime _scope;
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
        TurnSessionView session,
        SystemPromptAssembler promptAssembler,
        MemorySurfacer memorySurfacer,
        Action<string> onStatus,
        Action<bool> onGeneratingChanged,
        Action shutdownApp,
        Func<AgentLoopRequest, IAsyncEnumerable<AgentEvent>>? runLoop = null,
        TodoReminder? todo = null,
        AgentRuntime? scope = null)
    {
        _log = log;
        _chatState = chatState;
        _toolRegistry = toolRegistry;
        _stateBlocks = stateBlocks;
        _maintenance = maintenance;
        _serverProps = serverProps;
        _session = session;
        _promptAssembler = promptAssembler;
        _memorySurfacer = memorySurfacer;
        _onStatus = onStatus;
        _onGeneratingChanged = onGeneratingChanged;
        _shutdownApp = shutdownApp;
        _todo = todo;
        // null (тесты/старые точки сборки) — main-скоуп: поведение прежнее.
        _scope = scope ?? AgentRuntime.Main;
        _runLoop = runLoop ?? (request => new AgentLoop(_toolRegistry).RunAsync(request));
    }

    /// <summary>Токен активного хода (None, пока хода нет) — для live-реколла и фоновых проб.</summary>
    public CancellationToken ActiveToken => _cancellation?.Token ?? CancellationToken.None;

    /// <summary>Отменить активный ход.</summary>
    public void Cancel() => _cancellation?.Cancel();

    /// <summary>
    /// Провести ход: FSM-вход (единый вход) → бюджет-проверка (кроме continue) → AgentLoop → итог.
    /// onEvent — sink вида для событий цикла (пузыри); доменная логика — здесь.
    /// </summary>
    public async Task<TurnOutcome> RunTurnAsync(bool continueLastAssistant, Action<AgentEvent> onEvent)
    {
        // ЕДИНЫЙ ВХОД (фаза 1, план 2026-09-28): переход FSM — точка входа в ход, атомарна
        // на UI-потоке и идёт ДО бюджет-чека. Второй вызов во время хода (двойной клик
        // «отправить», wake/heartbeat, откреплённое окно) видит занятый FSM и грациозно
        // получает Busy — без исключения и сиротского хода. IsBusy-часть гварда: таблица
        // разрешает Compacting/AwaitingConfirmation → Generating (резюме хода внутри цикла),
        // но новый ход стартует только из Idle. RestartPending — терминален, TryTransition
        // упадёт в false — тоже Busy, не бросок.
        if (_chatState.IsBusy || !_chatState.TryTransition(ChatState.Generating))
        {
            return new TurnOutcome { Busy = true };
        }
        _onGeneratingChanged(true);
        _cancellation = new CancellationTokenSource();
        try
        {
            // Бюджет-проверка — после входа в FSM: её падение (сервер недоступен, /tokenize
            // не вернул точное число) раньше оставляло ход в тишине — fire-and-forget задача
            // (heartbeat/wake) гасла без следа, а добавленное user-сообщение «висело»
            // несохранённым. Показываем ошибку, сохраняем историю; FSM откатывается в Idle
            // в finally ниже.
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
                    _session.SaveCurrent();
                    // Громко в events-лог: повторяющийся budget-failed — сигнал к действию
                    // (бюджет ужат, контекст разросся, сервер не отвечает).
                    AppEventLog.Log($"turn: budget-failed — {exception.Message}");
                    return new TurnOutcome { BudgetFailed = true };
                }
            }
            // Режим всегда агентный (тумблер режимов убран из UI, 2026-08-22; гейт по
            // ProjectRoot убран планом 2026-10-01): любой чат инструментален, набор
            // инструментов определяет профиль (prompt.Tools). Рабочая папка — параметр
            // path-тулов: root сессии читается AgentLoop на каждой итерации.
            var outcome = await RunCoreAsync(continueLastAssistant, onEvent);
            LogTurnSummary(outcome);
            return outcome;
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            // FSM: Generating → Idle (если ещё не в RestartPending).
            // Сначала FSM, потом IsGenerating=false: уведомление CanExecuteChanged должно
            // стрельнуть, когда IsBusy уже false, иначе кнопка отправки останется серой.
            if (_chatState.Current == ChatState.Generating)
            {
                _chatState.Transition(ChatState.Idle);
            }
            _onGeneratingChanged(false);
        }
    }

    /// <summary>
    /// Реальный ход: FSM, AgentLoop, обработка отмены/ошибки, рестарт. Единый путь
    /// генерации (всегда агентный: тумблер режимов убран 2026-08-22, гейт по ProjectRoot
    /// убран 2026-10-01); allowToolExecution/toolDefinitions зависят от профиля чата
    /// (prompt.Tools) — рабочая папка на рекламу не влияет.
    /// </summary>
    private async Task<TurnOutcome> RunCoreAsync(bool continueLastAssistant, Action<AgentEvent> onEvent)
    {
        // FSM-вход (Idle → Generating) и возврат в Idle — в RunTurnAsync: единый вход
        // и единый выход хода (фаза 1, план 2026-09-28). Здесь — только тело хода.
        // CTS создаёт RunTurnAsync до вызова (единый вход) — здесь не-null гарантирован.
        var turnToken = _cancellation!.Token;
        var continued = continueLastAssistant && _log.Count > 0 &&
            _log[^1].Role == ChatRole.Assistant
            ? _log[^1]
            : null;
        // Статистики хода (P2): итерации/тулы/токены — из событий цикла, компакция —
        // из переходов FSM (единственный источник правды «сжимали ли контекст»).
        var stopwatch = Stopwatch.StartNew();
        var iterations = 0;
        var toolCalls = 0;
        var promptTokens = 0;
        var completionTokens = 0;
        var compacted = false;
        void OnStateChanged(ChatState from, ChatState to)
        {
            if (to == ChatState.Compacting)
            {
                compacted = true;
            }
        }
        _chatState.StateChanged += OnStateChanged;
        TurnOutcome outcome;
        try
        {
            var sessionDir = _session.Directory();
            var settings = AppSettings.Get();
            var multimodal = await MultimodalContext.BuildAsync(sessionDir, settings.Endpoint, _serverProps, turnToken);
            // Профиль чата: три независимых куска из статичного хранилища (default = как раньше).
            // main-агент ведётся идентичностью — промпт-кусок и отключение state-блока на него не действуют.
            var isMain = _session.SessionId() == MainAgent.SessionId;
            var profiles = ChatProfiles.Get();
            var sampler = profiles.ResolveSampler(_session.SamplerKey());
            var prompt = profiles.ResolvePrompt(_session.PromptKey());
            var stateEnabled = isMain || profiles.ResolveStateBlock(_session.StateBlockKey()).Enabled;
            var toolsAllowed = isMain || prompt.Tools;
            await foreach (var agentEvent in _runLoop(new AgentLoopRequest {
                Conversation = _log,
                // Скоуп рантайма: цикл читает профиль настроек и маршрут интерактива
                // из него (pinned-рантайм — собственный скоуп, не main).
                Runtime = _scope,
                OnFactSaved = item => _memorySurfacer.SurfaceOwnWrite(item.Id, item.Content),
                ContinueLastAssistant = continued is not null,
                AllowToolExecution = toolsAllowed,
                ToolDefinitions = toolsAllowed ? _promptAssembler.ToolsFor(prompt.AllowedTools, prompt.DeniedTools) : Array.Empty<ToolDefinition>(),
                Generation = settings.ToGenerationOptions(sampler),
                MaxIterations = settings.ResolveMaxIterations(sampler),
                // Nag самопроверки живёт ВНУТРИ state-блока — без блока nag'ать некуда.
                SanityCheckInterval = stateEnabled ? settings.ResolveSanityCheckInterval(sampler) : 0,
                ReasoningEffort = ParseEffort(prompt.ReasoningEffort),
                // После РЕАЛЬНОГО рендера в модель — сдвиг счётчиков: показов всплывших
                // памятей (mem-nag) и шагов TODO-напоминателя. Превью/подсчёт токенов
                // ходят через PromptPipeline без OnRendered — счётчики не двигаются.
                StateProvider = stateEnabled ? messages => {
                    var state = _stateBlocks.Build();
                    _memorySurfacer.OnRendered();
                    _todo?.OnRendered();
                    return state;
                } : null,
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
                SlotId = _session.SlotId(),
                SessionRoot = _session.Root,
                SetSessionRoot = _session.SetRoot,
                CancellationToken = turnToken
            }))
            {
                // Счётчики до sink'а: статистика хода не зависит от вида.
                switch (agentEvent)
                {
                    case AssistantMessageEvent assistant:
                        iterations++;
                        if (assistant.Message.Generation is { } generation)
                        {
                            promptTokens += generation.PromptTokens ?? 0;
                            completionTokens += generation.CompletionTokens ?? 0;
                        }
                        break;
                    case ToolCallStartedEvent:
                        toolCalls++;
                        break;
                }
                onEvent(agentEvent);
            }
            outcome = FinishOutcome();
        }
        catch (OperationCanceledException)
        {
            outcome = FinishOutcome(canceled: true);
        }
        catch (Exception exception)
        {
            // Куда показать ошибку (пузырь или статус) — решает вид: получает исключение в итоге.
            outcome = FinishOutcome(error: exception);
        }
        finally
        {
            _chatState.StateChanged -= OnStateChanged;
        }
        if (SelfBuildService.ConsumeRestartRequest() is { } restartBuildId)
        {
            RebuildEventLog.App($"rebuild: turn-end — restart.request consumed (build {restartBuildId}), app will exit; launcher deploys");
            RestartInto(restartBuildId);
        }
        return outcome;

        // Частичные статистики при отмене/ошибке — это и есть данные для постмортема.
        TurnOutcome FinishOutcome(bool canceled = false, Exception? error = null) => new()
        {
            Canceled = canceled,
            Error = error,
            Iterations = iterations,
            ToolCalls = toolCalls,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            Compacted = compacted,
            SlotId = _session.SlotId(),
            Duration = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// Саммари хода в always-on events-лог (P2, 2026-10-01): одна grep'ящаяся строка
    /// «turn: ...» на ход — постмортем для длинных автономных прогонов (events-лог +
    /// crash-лог вместе дают картину: что делал ход, сколько стоил, где упал).
    /// </summary>
    private static void LogTurnSummary(TurnOutcome outcome)
    {
        var kind = outcome.Canceled ? "canceled" : outcome.Error is not null ? "error" : "ok";
        var slot = outcome.SlotId is { } slotId ? $"slot {slotId}" : "slot -";
        var compacted = outcome.Compacted ? "compacted " : string.Empty;
        var duration = $"{(int)outcome.Duration.TotalMinutes}m{(int)outcome.Duration.TotalSeconds % 60}s";
        var detail = outcome.Error is { } error
            ? $" | {error.Message.ReplaceLineEndings(" ")}"
            : string.Empty;
        AppEventLog.Log($"turn: {kind} | {outcome.Iterations} iter | {outcome.ToolCalls} tools | " +
                        $"{outcome.PromptTokens} prompt / {outcome.CompletionTokens} completion tok | " +
                        $"{compacted}{slot} | {duration}{detail}");
    }

    /// <summary>
    /// Рестарт в новую сборку: сохранить историю, запустить launcher (pointer-режим),
    /// закрыть процесс (делегат UI — Core не знает про WPF).
    /// </summary>
    private void RestartInto(string buildId)
    {
        _session.SaveCurrent();
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

/// <summary>
/// Вид сессии для хода: профиль-ключи, каталог, сейв. Главное окно — backed SessionController
/// (текущая сессия, селектор); окна субагентов (мультиоконный квест, стадия C) — закреплённые
/// за своей сессией. TurnPipeline не знает про контроллер — это шов закреплённого рантайма.
/// </summary>
public sealed record TurnSessionView(
    Func<string> SessionId,
    Func<string> Directory,
    Func<string?> SamplerKey,
    Func<string?> PromptKey,
    Func<string?> StateBlockKey,
    Action SaveCurrent,
    /// <summary>Пиннинг слота llama.cpp для сессии (SlotAllocation): main → 0, окна → 1/2, субагент → 3.</summary>
    Func<int?> SlotId,
    /// <summary>
    /// Рабочая папка сессии (root инструментов, план 2026-10-01): живое значение —
    /// тул set_session_root меняет его в середине хода, следующий рендер/итерация видит.
    /// null — не задано: фоллбек в глобальную AppSettings.ProjectRoot.
    /// </summary>
    Func<string?>? Root = null,
    /// <summary>
    /// Назначить рабочую папку сессии (тул set_session_root / UI). Возвращает текст
    /// ошибки; null — успех (сессия сохранена). null — в этом контексте нельзя.
    /// </summary>
    Func<string, string?>? SetRoot = null);

/// <summary>
/// Итог хода: вид решает, куда показать ошибку (пузырь ответа или статус-строка).
/// Статистики (P2, 2026-10-01): ход — единственная единица наблюдения harness'а;
/// саммари пишется в always-on events-лог (LogTurnSummary) — постмортем для длинных
/// автономных прогонов. При отмене/ошибке несутся ЧАСТИЧНЫЕ статистики — они и есть
/// данные для постмортема (где и на какой итерации упал).
/// </summary>
public sealed class TurnOutcome
{
    /// <summary>Ход отменён (Cancel).</summary>
    public bool Canceled { get; init; }
    /// <summary>Ход не стартовал: не прошла проверка бюджета контекста (статус и сейв уже сделаны).</summary>
    public bool BudgetFailed { get; init; }
    /// <summary>Ход не стартовал: FSM занят (второй вход — двойной клик «отправить», wake во время хода).</summary>
    public bool Busy { get; init; }
    /// <summary>Ход упал (цикл бросил).</summary>
    public Exception? Error { get; init; }
    /// <summary>Итераций цикла (ассистент-сообщений = вызовов модели).</summary>
    public int Iterations { get; init; }
    /// <summary>Вызовов инструментов (ToolCallStarted).</summary>
    public int ToolCalls { get; init; }
    /// <summary>Сумма prompt-токенов по итерациям (из GenerationInfo сервера).</summary>
    public int PromptTokens { get; init; }
    /// <summary>Сумма completion-токенов по итерациям.</summary>
    public int CompletionTokens { get; init; }
    /// <summary>Был ли переход FSM в Compacting за ход.</summary>
    public bool Compacted { get; init; }
    /// <summary>Слот llama.cpp хода (SlotAllocation); null — без пиннинга.</summary>
    public int? SlotId { get; init; }
    /// <summary>Длительность хода (старт RunCoreAsync → итог).</summary>
    public TimeSpan Duration { get; init; }
}
