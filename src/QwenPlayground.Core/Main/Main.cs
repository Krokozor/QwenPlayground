using System.Reflection;
using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Heartbeat;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Runtime;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Templates;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Main;

/// <summary>
/// Хуки в оболочку (UI): фасад не знает про WPF — статус-строка, флаг генерации,
/// окошко ввода (драфт), heartbeat-ход (пузырь + генерация), flush памяти,
/// меню полок, shutdown процесса.
/// </summary>
public sealed record UiHooks(
    Action<string> Status,
    Action<bool> Generating,
    Func<string> DraftInput,
    Action<string> DraftInputSet,
    Action SaveCurrent,
    Func<string, Task> HeartbeatStartTurn,
    Func<Task> FlushMemory,
    Action OnCompactedUi,
    Action ShutdownApp);

/// <summary>
/// Композиционный корень main-агента (паттерн NekoBot): владеет всем графом
/// сервисов и знает порядок их сборки. UI (MainViewModel) — протокол-адаптер:
/// пузыри, команды, тонкие виды настроек; качает UI-таймеры, регистрирует
/// UI-сервисы жизненного цикла и вызывает Lifecycle.StartAll.
///
/// Конструктор ничего не стартует (StartAll — за UI, после регистрации
/// UI-сервисов) и не загружает сессии — за адаптером (вид должен быть готов).
/// </summary>
public sealed class Main
{
    private readonly InjectedIdentity _identity = new();
    private readonly ExternalToolsNote _externalTools = new();
    private readonly MemoryLayerStore _layerStore = new();

    public ChatLog Log { get; } = new();
    public ChatStateMachine ChatState { get; } = new();
    public ToolRegistry Tools { get; }
    public ServerProps ServerProps { get; } = new();
    public CompactionPreview Compaction { get; } = new();
    public MemorySurfacer MemorySurfacer { get; } = new();
    public PairsStore PairsStore { get; }
    public BackgroundWork Background { get; }
    public ServiceCompletionClient ServiceLlm { get; }
    public SystemPromptAssembler PromptAssembler { get; }
    public StateBlockBuilder StateBlocks { get; }
    public PromptPipeline Pipeline { get; }
    public ContextMaintenance Maintenance { get; }
    public AppLifecycle Lifecycle { get; }
    public DraftKeeper Draft { get; }
    public SessionController Sessions { get; }
    public TurnPipeline Turns { get; }
    public HeartbeatController Heartbeat { get; }

    public Main(UiHooks hooks, params Assembly[] toolAssemblies)
    {
        Tools = new ToolRegistry(toolAssemblies);
        PairsStore = new PairsStore(new MemoryStore().Root);
        Background = new BackgroundWork(hooks.Status);
        // Сервисные LLM-вызовы (суммаризация/компакция/конвейер/память): эндпоинт и
        // семплер вычисляются на каждый вызов из живых настроек.
        ServiceLlm = new ServiceCompletionClient(
            () => AppSettings.Get().Endpoint,
            () => AppSettings.Get().ToGenerationOptions(ServiceCompletionClient.MaxTokens));
        PromptAssembler = new SystemPromptAssembler(
            () => Sessions.CurrentId,
            () => Sessions.PromptKey,
            () => Sessions.DirectoryFor(Sessions.CurrentId),
            _identity,
            _externalTools,
            Tools);
        // Доска сообщений state-блока: pull-анонсеры (состояние на момент рендера) +
        // BoardAnnouncer — дрейн статичной мусорки (push из кода без интерфейса).
        StateBlocks = new StateBlockBuilder(
            Log.AssignPendingIds,
            () => Log.NextMessageId,
            () => EffectiveContextSize,
            ServerProps,
            () => Log,
            () => MemorySurfacer.GetSurfacedForStateBlock(),
            [MemorySurfacer, new BoardAnnouncer()],
            () => PairsStore.Pending);
        Pipeline = new PromptPipeline(
            () => Log,
            () => PromptAssembler.ResolveSystemPrompt(),
            Tools,
            ServerProps,
            messages => StateBlocks.Build(),
            ct => MultimodalContext.BuildAsync(Sessions.DirectoryFor(Sessions.CurrentId), AppSettings.Get().Endpoint, ServerProps, ct),
            activeShelves: () => PromptAssembler.EffectiveShelves());
        Maintenance = new ContextMaintenance(
            Log,
            ChatState,
            Compaction,
            (user, system, onChunk, ct) => ServiceLlm.CompleteStructuredAsync(user, system, onChunk, ct),
            _layerStore,
            MemorySurfacer,
            ct => Pipeline.CountNextTokensAsync(ct),
            async () =>
            {
                // Реальный n_ctx сервера (кэш), иначе настроенный ContextSize.
                await ServerProps.FetchAsync(AppSettings.Get().Endpoint);
                return EffectiveContextSize;
            },
            () => Sessions.CurrentId,
            new ContextBackupStore(ChatSessions.Root),
            new ContextMaintenance.Ui(hooks.Status, hooks.Generating, hooks.SaveCurrent),
            onCompacted: () =>
            {
                PromptAssembler.DeactivateUnusedShelves(Log);
                hooks.OnCompactedUi();
            });
        // Жизненный цикл: единая точка старта/остановки сервисов (закрытие — LIFO, без бросков).
        Lifecycle = new AppLifecycle(hooks.Status);
        // Драфт окошка ввода: создаём ДО сессий — Load пользуется Draft (Flush/Restore).
        Draft = new DraftKeeper(
            hooks.DraftInput,
            hooks.DraftInputSet,
            () => Sessions.CurrentId,
            new SessionDraftStore(ChatSessions.Root),
            () => AppSettings.Get().DraftSaveIntervalSeconds);
        // Сессии: после драфта (Load пользуется драфтом).
        Sessions = new SessionController(Log, Draft, MemorySurfacer);
        // Ход: после сессий (пользуется их ключами/каталогом) и maintenance (бюджет-гард).
        Turns = new TurnPipeline(
            Log,
            ChatState,
            Tools,
            StateBlocks,
            Maintenance,
            ServerProps,
            Sessions,
            PromptAssembler,
            MemorySurfacer,
            hooks.Status,
            hooks.Generating,
            hooks.ShutdownApp);
        // Heartbeat: опрос wake/ и расписания. Период опроса фиксированный (20 с),
        // частота реальных пробуждений — HeartbeatIntervalMinutes; сигналы не ждут расписания.
        Heartbeat = new HeartbeatController(
            new WakeSignalStore(),
            isBusy: () => ChatState.IsBusy,
            heartbeatEnabled: () => AppSettings.Get().HeartbeatEnabled,
            heartbeatIntervalMinutes: () => AppSettings.Get().HeartbeatIntervalMinutes,
            setStatus: hooks.Status,
            startTurn: hooks.HeartbeatStartTurn,
            flushMemory: hooks.FlushMemory,
            watchdogGuard: WatchdogLauncher.EnsureAlive);
    }

    /// <summary>
    /// Эффективный размер окна: реальный n_ctx сервера (если известен), иначе настроенный
    /// ContextSize. Для проверки «влезет ли» сравниваем именно с ним — это то, что реально
    /// спрашивает сервер.
    /// </summary>
    public int EffectiveContextSize =>
        Math.Min(AppSettings.Get().ContextSize, ServerProps.NContext ?? AppSettings.Get().ContextSize);
}
