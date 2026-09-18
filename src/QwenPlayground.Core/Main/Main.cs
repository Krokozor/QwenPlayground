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

    // ── Общие сервисы (на всё приложение) ───────────────────────────────────────────
    public ToolRegistry Tools { get; }
    public ServerProps ServerProps { get; } = new();
    public PairsStore PairsStore { get; }
    public BackgroundWork Background { get; }
    public ServiceCompletionClient ServiceLlm { get; }
    public AppLifecycle Lifecycle { get; }
    public SessionController Sessions { get; }
    public HeartbeatController Heartbeat { get; }

    // ── Рантайм main-агента (пер-разговорные сервисы; форварды для совместимости) ──
    public ChatRuntime Runtime { get; }
    public ChatLog Log => Runtime.Log;
    public ChatStateMachine ChatState => Runtime.ChatState;
    public CompactionPreview Compaction => Runtime.Compaction;
    public MemorySurfacer MemorySurfacer => Runtime.MemorySurfacer;
    public SystemPromptAssembler PromptAssembler => Runtime.PromptAssembler;
    public StateBlockBuilder StateBlocks => Runtime.StateBlocks;
    public PromptPipeline Pipeline => Runtime.Pipeline;
    public ContextMaintenance Maintenance => Runtime.Maintenance;
    public DraftKeeper Draft => Runtime.Draft;
    public TurnPipeline Turns => Runtime.Turns;
    public int EffectiveContextSize => Runtime.EffectiveContextSize;

    public Main(UiHooks hooks, params Assembly[] toolAssemblies)
    {
        // ── Общие сервисы (на всё приложение) ────────────────────────────────────────
        Tools = new ToolRegistry(toolAssemblies);
        PairsStore = new PairsStore(new MemoryStore().Root);
        Background = new BackgroundWork(hooks.Status);
        // Сервисные LLM-вызовы (суммаризация/компакция/конвейер/память): эндпоинт и
        // семплер вычисляются на каждый вызов из живых настроек.
        ServiceLlm = new ServiceCompletionClient(
            () => AppSettings.Get().Endpoint,
            () => AppSettings.Get().ToGenerationOptions(ServiceCompletionClient.MaxTokens));
        // Жизненный цикл: единая точка старта/остановки сервисов (закрытие — LIFO, без бросков).
        Lifecycle = new AppLifecycle(hooks.Status);

        // ── Рантайм main-агента: пер-разговорные сервисы (бандл — ChatRuntime) ──────
        // Сессия динамична: селектор главного окна переключает сессии, хуки ссылаются
        // на Sessions лениво (заполняется ниже).
        Runtime = new ChatRuntime(() => Sessions.CurrentId, ServerProps);
        var rt = Runtime;
        rt.PromptAssembler = new SystemPromptAssembler(
            rt.SessionId,
            () => Sessions.PromptKey,
            () => Sessions.DirectoryFor(Sessions.CurrentId),
            _identity,
            _externalTools,
            Tools);
        // Доска сообщений state-блока: pull-анонсеры (состояние на момент рендера) +
        // BoardAnnouncer — дрейн статичной мусорки (push из кода без интерфейса).
        rt.StateBlocks = new StateBlockBuilder(
            rt.Log.AssignPendingIds,
            () => rt.Log.NextMessageId,
            () => rt.EffectiveContextSize,
            ServerProps,
            () => rt.Log,
            () => rt.MemorySurfacer.GetSurfacedForStateBlock(),
            [rt.MemorySurfacer, new BoardAnnouncer()],
            () => PairsStore.Pending);
        rt.Pipeline = new PromptPipeline(
            () => rt.Log,
            () => rt.PromptAssembler.ResolveSystemPrompt(),
            Tools,
            ServerProps,
            messages => rt.StateBlocks.Build(),
            ct => MultimodalContext.BuildAsync(Sessions.DirectoryFor(Sessions.CurrentId), AppSettings.Get().Endpoint, ServerProps, ct),
            activeShelves: () => rt.PromptAssembler.EffectiveShelves());
        rt.Maintenance = new ContextMaintenance(
            rt.Log,
            rt.ChatState,
            rt.Compaction,
            (user, system, onChunk, ct) => ServiceLlm.CompleteStructuredAsync(user, system, onChunk, ct),
            _layerStore,
            rt.MemorySurfacer,
            ct => rt.Pipeline.CountNextTokensAsync(ct),
            async () =>
            {
                // Реальный n_ctx сервера (кэш), иначе настроенный ContextSize.
                await ServerProps.FetchAsync(AppSettings.Get().Endpoint);
                return rt.EffectiveContextSize;
            },
            rt.SessionId,
            new ContextBackupStore(ChatSessions.Root),
            new ContextMaintenance.Ui(hooks.Status, hooks.Generating, hooks.SaveCurrent),
            onCompacted: () =>
            {
                rt.PromptAssembler.DeactivateUnusedShelves(rt.Log);
                hooks.OnCompactedUi();
            });
        // Драфт окошка ввода: создаём ДО сессий — Load пользуется Draft (Flush/Restore).
        rt.Draft = new DraftKeeper(
            hooks.DraftInput,
            hooks.DraftInputSet,
            rt.SessionId,
            new SessionDraftStore(ChatSessions.Root),
            () => AppSettings.Get().DraftSaveIntervalSeconds);
        // Сессии: после драфта (Load пользуется драфтом).
        Sessions = new SessionController(rt.Log, rt.Draft, rt.MemorySurfacer);
        // Ход: после сессий (пользуется их ключами/каталогом) и maintenance (бюджет-гард).
        rt.Turns = new TurnPipeline(
            rt.Log,
            rt.ChatState,
            Tools,
            rt.StateBlocks,
            rt.Maintenance,
            ServerProps,
            new TurnSessionView(
                () => Sessions.CurrentId,
                () => Sessions.DirectoryFor(Sessions.CurrentId),
                () => Sessions.SamplerKey,
                () => Sessions.PromptKey,
                () => Sessions.StateBlockKey,
                Sessions.SaveCurrent),
            rt.PromptAssembler,
            rt.MemorySurfacer,
            hooks.Status,
            hooks.Generating,
            hooks.ShutdownApp);
        // ── Main-специфичное ─────────────────────────────────────────────────────────
        // Heartbeat: опрос wake/ и расписания. Период опроса фиксированный (20 с),
        // частота реальных пробуждений — HeartbeatIntervalMinutes; сигналы не ждут расписания.
        Heartbeat = new HeartbeatController(
            new WakeSignalStore(),
            isBusy: () => rt.ChatState.IsBusy,
            heartbeatEnabled: () => AppSettings.Get().HeartbeatEnabled,
            heartbeatIntervalMinutes: () => AppSettings.Get().HeartbeatIntervalMinutes,
            setStatus: hooks.Status,
            startTurn: hooks.HeartbeatStartTurn,
            flushMemory: hooks.FlushMemory,
            watchdogGuard: WatchdogLauncher.EnsureAlive);
    }

    /// <summary>
    /// Рантайм, закреплённый за сессией (окно субагента, мультиоконный квест, стадия C):
    /// общие сервисы (Tools/ServerProps/ServiceLlm) шарятся с приложением, сессия фиксирована
    /// — селектор главного окна на неё не влияет. Ключи профилей — из параметров (null = default);
    /// хуки — UI окна. Идентичность main-агента не участвует (не-main сессия — профиль-промпт).
    /// </summary>
    public ChatRuntime CreatePinnedRuntime(string sessionId, UiHooks hooks,
        string? samplerKey = null, string? promptKey = null, string? stateBlockKey = null)
    {
        var rt = new ChatRuntime(() => sessionId, ServerProps);
        rt.PromptAssembler = new SystemPromptAssembler(
            rt.SessionId,
            () => promptKey,
            () => Sessions.DirectoryFor(sessionId),
            new InjectedIdentity(),
            new ExternalToolsNote(),
            Tools);
        rt.StateBlocks = new StateBlockBuilder(
            rt.Log.AssignPendingIds,
            () => rt.Log.NextMessageId,
            () => rt.EffectiveContextSize,
            ServerProps,
            () => rt.Log,
            () => rt.MemorySurfacer.GetSurfacedForStateBlock(),
            [rt.MemorySurfacer, new BoardAnnouncer()],
            () => PairsStore.Pending);
        rt.Pipeline = new PromptPipeline(
            () => rt.Log,
            () => rt.PromptAssembler.ResolveSystemPrompt(),
            Tools,
            ServerProps,
            messages => rt.StateBlocks.Build(),
            ct => MultimodalContext.BuildAsync(Sessions.DirectoryFor(sessionId), AppSettings.Get().Endpoint, ServerProps, ct),
            activeShelves: () => rt.PromptAssembler.EffectiveShelves());
        rt.Maintenance = new ContextMaintenance(
            rt.Log,
            rt.ChatState,
            rt.Compaction,
            (user, system, onChunk, ct) => ServiceLlm.CompleteStructuredAsync(user, system, onChunk, ct),
            new MemoryLayerStore(),
            rt.MemorySurfacer,
            ct => rt.Pipeline.CountNextTokensAsync(ct),
            async () =>
            {
                await ServerProps.FetchAsync(AppSettings.Get().Endpoint);
                return rt.EffectiveContextSize;
            },
            rt.SessionId,
            new ContextBackupStore(ChatSessions.Root),
            new ContextMaintenance.Ui(hooks.Status, hooks.Generating, hooks.SaveCurrent),
            onCompacted: () =>
            {
                rt.PromptAssembler.DeactivateUnusedShelves(rt.Log);
                hooks.OnCompactedUi();
            });
        rt.Draft = new DraftKeeper(
            hooks.DraftInput,
            hooks.DraftInputSet,
            rt.SessionId,
            new SessionDraftStore(ChatSessions.Root),
            () => AppSettings.Get().DraftSaveIntervalSeconds);
        rt.Turns = new TurnPipeline(
            rt.Log,
            rt.ChatState,
            Tools,
            rt.StateBlocks,
            rt.Maintenance,
            ServerProps,
            new TurnSessionView(
                rt.SessionId,
                () => Sessions.DirectoryFor(sessionId),
                () => samplerKey,
                () => promptKey,
                () => stateBlockKey,
                () => Sessions.SavePinned(sessionId, rt.Log, samplerKey, promptKey, stateBlockKey)),
            rt.PromptAssembler,
            rt.MemorySurfacer,
            hooks.Status,
            hooks.Generating,
            hooks.ShutdownApp);
        return rt;
    }
}
