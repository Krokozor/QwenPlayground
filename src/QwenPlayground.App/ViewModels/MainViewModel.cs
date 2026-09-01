using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Heartbeat;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Probes;
using QwenPlayground.Core.Runtime;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Templates;
using QwenPlayground.Core.Tools;
namespace QwenPlayground.App.ViewModels;

public partial class MainViewModel : ObservableObject {
    private static readonly string SessionsRoot = ChatSessions.Root;
    // Жизненный цикл сессий (текущий id, список, миграция, «последняя открытая»).
    private readonly ChatSessions _sessions = new();
    // Динамический системный промпт main-агента (identity+layers+trajectory), кэш по mtime.
    private readonly InjectedIdentity _identity = new();
    private readonly ChatLog _log = new();
    // Структурные изменения разговора (компакция/загрузка/откат) сами перестраивают вид.
    private void OnLogChanged() => RebuildMessageViews();
    // Две сборки: Core (базовые инструменты) + App (UI-инструменты: screenshot, switch_tab).
    private readonly ToolRegistry _toolRegistry = new(typeof(AgentTool).Assembly, typeof(MainViewModel).Assembly);
    /// <summary>Каталог текущей сессии: у каждой сессии своя папка sessions/&lt;id&gt;/ (как у main-агента).</summary>
    private string SessionDir() => _sessions.DirectoryFor(_sessions.CurrentId);
    private readonly MemoryLayerStore _layerStore = new();
    private CancellationTokenSource? _cancellation;
    // Владелец фоновой работы: «запустил и забыл» с гарантией, что исключение не умрёт в тишине.
    private readonly BackgroundWork _background;

    /// <summary>UI-диспетчер ходов (heartbeat/wake/flush видны списком, не одной строкой).</summary>
    public TurnPanel TurnsPanel { get; private set; } = null!;
    // Сервисные LLM-вызовы (суммаризация/компакция/конвейер/память): эндпоинт и семплер
    // вычисляются на каждый вызов из живых настроек (инициализация в конструкторе).
    private readonly ServiceCompletionClient _serviceLlm;
    // Сердцебиение: решение «когда и чем будить» — в HeartbeatController (тестируемо),
    // исполнение хода/flush — здесь.
    private readonly HeartbeatController _heartbeat;
    // Оконный интерактив инструментов (ask_user/подтверждение shell) поверх FSM.
    private readonly ChatInteraction _interaction;
    // Жизненный цикл: реестр стартуемых/останавливаемых сервисов.
    private readonly AppLifecycle _lifecycle;
    private readonly ChatStateMachine _chatState = new();
    // Сжатие контекста и бюджет-обслуживание (домен вынесен; FSM-контракт — см. класс).
    private readonly ContextMaintenance _maintenance;
    // Live-превью компакции (буфер + троттл + панель) — в Core-модели CompactionPreview.
    private readonly CompactionPreview _compaction = new();
    // Свойства сервера (media_marker + n_ctx + последний фактический подсчёт токенов):
    // кэш GET /props на TTL; счётчик промпта пишется конвейером после /tokenize.
    private readonly ServerProps _serverProps = new();
    // Сборка следующего промпта + точный подсчёт токенов (превью, бюджет-гвард, state-блок).
    private readonly PromptPipeline _pipeline;
    // Снапшот самосостояния агента для рендера (msg_id/время/контекст/сборка/воспоминания).
    private readonly StateBlockBuilder _stateBlocks;
    // Ассоциативная память: всплывшие факты складываются в state-блок, живут до компакции,
    // дубликаты по id не повторяются; live-реколл во время генерации + наг менеджмента памяти.
    // Реализация — в Core/Memory/MemorySurfacer (тестируема в isolation).
    private readonly MemorySurfacer _memorySurfacer = new();
    // Связи пар воспоминаний (очередь надмоза на слияние + разведённые false-positive).
    private readonly PairsStore _pairsStore = new(new MemoryStore().Root);

    // Flush-векторизация памяти (NekoBot): фоновая до-классификация фактов без слоёв/с устаревшей
    // версией словаря. Троттлинг — раз в минуту, до 2 фактов за проход; в горячем потоке чата не бегает.
    private DateTime _lastMemoryFlushAt = DateTime.MinValue;
    private bool _memoryFlushInFlight;
    private static readonly TimeSpan MemoryFlushInterval = TimeSpan.FromMinutes(1);
    /// <summary>Вкладка «Диагностика»: состояние FSM, бюджет контекста, сборки, память.</summary>
    public DiagnosticsViewModel Diagnostics { get; }

    /// <summary>Вкладка «Память»: витрина-валидатор классификации и реколла.</summary>
    public MemoryViewModel Memory { get; } = new();
    /// <summary>
    /// Вкладка «Суммаризация»: инспекция и правка резюме сессий, слоёв L1/L2/L3,
    /// промптов (config/prompts.json) и ре-прогоны суммаризации. Ходит в LLM через
    /// тот же эндпоинт и семплер, что и компакция (RunSummarizationCallAsync).
    /// </summary>
    public SummarizationViewModel Summarization { get; }

    // ── Настройки ───────────────────────────────────────────────────────────────────── //
    // Паттерн NekoBot: источник правды — синглтон AppSettings.Get(), свойства ниже —
    // тонкие виды над ним (чтение напрямую, запись = мутация + INPC + отложенный Save).
    // Зеркальные поля и маппинг ToSettings/ApplySettings упразднены: новое поле настроек
    // добавляется в AppSettings + сюда одним свойством, без правки списков персистенции.

    /// <summary>Запись настройки с уведомлением биндинга и отложенным сохранением.</summary>
    private void Set<T>(T current, T value, Action<AppSettings, T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null) {
        if (EqualityComparer<T>.Default.Equals(current, value)) 
            return;
        
        var settings = AppSettings.Get();
        assign(settings, value);
        OnPropertyChanged(propertyName);
        ScheduleSettingsSave();
    }

    /// <summary>Читаемая настройка: <c>S.Endpoint</c> короче, чем AppSettings.Get().Endpoint, в 17 свойствах.</summary>
    private AppSettings S => AppSettings.Get();

    /// <summary>Адрес llama.cpp-сервера основного хода.</summary>
    public string Endpoint {
        get => S.Endpoint;
        set {
            var old = S.Endpoint;
            Set(old, value, (s, v) => s.Endpoint = v);
            if (old != S.Endpoint) {
                SendCommand.NotifyCanExecuteChanged();                
            }
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand))]
    [NotifyCanExecuteChangedFor(nameof(RollbackCommand))]
    [NotifyCanExecuteChangedFor(nameof(RerollCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyChatCommand))]
    private bool _isGenerating;
    public int MaxTokens {
        get => S.MaxTokens;
        set => Set(S.MaxTokens, value, (s, v) => s.MaxTokens = v);
    }

    public int ContextSize {
        get => S.ContextSize;
        set => Set(S.ContextSize, value, (s, v) => s.ContextSize = v);
    }

    /// <summary>Чат занят (нельзя принимать новые ходы/ручную компакцию). Вычисляется из FSM.</summary>
    public bool IsBusy => _chatState.IsBusy;

    /// <summary>Живое превью компакции (панель, стадии, стриминг токенов).</summary>
    public CompactionPreview Compaction => _compaction;

    public ReasoningEffort ReasoningEffort {
        get => S.ReasoningEffort;
        set {
            Set(S.ReasoningEffort, value, (s, v) => s.ReasoningEffort = v);
            OnPropertyChanged(nameof(ReasoningEffortIndex));
            RefreshPromptPreview();
        }
    }

    /// <summary>Усилие размышления (эталон из assets/chat_template.jinja): xhigh / medium / low.</summary>
    public int ReasoningEffortIndex {
        get => ReasoningEffort switch { ReasoningEffort.XHigh => 0, ReasoningEffort.Medium => 1, _ => 2 };
        set {
            ReasoningEffort = value switch {
                0 => ReasoningEffort.XHigh,
                1 => ReasoningEffort.Medium,
                _ => ReasoningEffort.Low };
            OnPropertyChanged();
        }
    }

    public bool HeartbeatEnabled {
        get => S.HeartbeatEnabled;
        set => Set(S.HeartbeatEnabled, value, (s, v) => s.HeartbeatEnabled = v);
    }

    public int HeartbeatIntervalMinutes {
        get => S.HeartbeatIntervalMinutes;
        set => Set(S.HeartbeatIntervalMinutes, value, (s, v) => s.HeartbeatIntervalMinutes = v);
    }

    public int MaxIterations {
        get => S.MaxIterations;
        set => Set(S.MaxIterations, value, (s, v) => s.MaxIterations = v);
    }

    public int SanityCheckInterval {
        get => S.SanityCheckInterval;
        set => Set(S.SanityCheckInterval, value, (s, v) => s.SanityCheckInterval = v);
    }

    /// <summary>Компаньон-модель (логит-пробы, векторизация памяти) — отдельная машина.</summary>
    public string CompanionEndpoint {
        get => S.CompanionEndpoint;
        set => Set(S.CompanionEndpoint, value, (s, v) => s.CompanionEndpoint = v);
    }
    public string CompactKeepRatio {
        get => S.CompactKeepRatio;
        set => Set(S.CompactKeepRatio, value, (s, v) => s.CompactKeepRatio = v);
    }
    public string ProjectRoot {
        get => S.ProjectRoot;
        set => Set(S.ProjectRoot, value, (s, v) => s.ProjectRoot = v);
    }

    [ObservableProperty]
    private string _statusText = string.Empty;

    public string Temperature {
        get => S.Temperature;
        set => Set(S.Temperature, value, (s, v) => s.Temperature = v);
    }

    public string TopP {
        get => S.TopP;
        set => Set(S.TopP, value, (s, v) => s.TopP = v);
    }

    public string TopK {
        get => S.TopK;
        set => Set(S.TopK, value, (s, v) => s.TopK = v);
    }

    public string MinP {
        get => S.MinP;
        set => Set(S.MinP, value, (s, v) => s.MinP = v);
    }

    public string RepeatPenalty {
        get => S.RepeatPenalty;
        set => Set(S.RepeatPenalty, value, (s, v) => s.RepeatPenalty = v);
    }

    public string Seed {
        get => S.Seed;
        set => Set(S.Seed, value, (s, v) => s.Seed = v);
    }

    // ── Память / надмозг ─────────────────────────────────────────────────────────────

    public int MemoryFlushBudget {
        get => S.MemoryFlushBudget;
        set => Set(S.MemoryFlushBudget, value, (s, v) => s.MemoryFlushBudget = v);
    }
    public int MemoryScanProbeBudget {
        get => S.MemoryScanProbeBudget;
        set => Set(S.MemoryScanProbeBudget, value, (s, v) => s.MemoryScanProbeBudget = v);
    }
    public int MemorySurfacingThreshold {
        get => S.MemorySurfacingThreshold;
        set => Set(S.MemorySurfacingThreshold, value, (s, v) => s.MemorySurfacingThreshold = v);
    }
    public int MemoryLiveRecallMinTokens {
        get => S.MemoryLiveRecallMinTokens;
        set => Set(S.MemoryLiveRecallMinTokens, value, (s, v) => s.MemoryLiveRecallMinTokens = v);
    }
    public int MemoryLiveRecallIntervalSec {
        get => S.MemoryLiveRecallIntervalSec;
        set => Set(S.MemoryLiveRecallIntervalSec, value, (s, v) => s.MemoryLiveRecallIntervalSec = v);
    }
    public int MemoryNagIntervalRenders {
        get => S.MemoryNagIntervalRenders;
        set => Set(S.MemoryNagIntervalRenders, value, (s, v) => s.MemoryNagIntervalRenders = v);
    }
    public int RecallTopX {
        get => S.RecallTopX;
        set => Set(S.RecallTopX, value, (s, v) => s.RecallTopX = v);
    }
    public string RecallMinScore {
        get => S.RecallMinScore.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Set(S.RecallMinScore, v, (s, x) => s.RecallMinScore = x); }
    }
    public string SimilaritySimilarMin {
        get => S.SimilaritySimilarMin.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Set(S.SimilaritySimilarMin, v, (s, x) => s.SimilaritySimilarMin = x); }
    }
    public string SimilarityDistinctMax {
        get => S.SimilarityDistinctMax.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Set(S.SimilarityDistinctMax, v, (s, x) => s.SimilarityDistinctMax = x); }
    }
    public string SimilarityConfidentMaxEntropy {
        get => S.SimilarityConfidentMaxEntropy.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Set(S.SimilarityConfidentMaxEntropy, v, (s, x) => s.SimilarityConfidentMaxEntropy = x); }
    }
    public int MemoryDialogueBudgetTokens {
        get => S.MemoryDialogueBudgetTokens;
        set => Set(S.MemoryDialogueBudgetTokens, value, (s, v) => s.MemoryDialogueBudgetTokens = v);
    }
    public int MemoryDialogueMaxMessages {
        get => S.MemoryDialogueMaxMessages;
        set => Set(S.MemoryDialogueMaxMessages, value, (s, v) => s.MemoryDialogueMaxMessages = v);
    }
    public int MemoryClassifyNProbs {
        get => S.MemoryClassifyNProbs;
        set => Set(S.MemoryClassifyNProbs, value, (s, v) => s.MemoryClassifyNProbs = v);
    }
    public int MemoryClassifyNPredict {
        get => S.MemoryClassifyNPredict;
        set => Set(S.MemoryClassifyNPredict, value, (s, v) => s.MemoryClassifyNPredict = v);
    }
    public int MemoryRerankNProbs {
        get => S.MemoryRerankNProbs;
        set => Set(S.MemoryRerankNProbs, value, (s, v) => s.MemoryRerankNProbs = v);
    }
    public int MemoryRerankNPredict {
        get => S.MemoryRerankNPredict;
        set => Set(S.MemoryRerankNPredict, value, (s, v) => s.MemoryRerankNPredict = v);
    }
    public int MemoryRerankMaxCandidates {
        get => S.MemoryRerankMaxCandidates;
        set => Set(S.MemoryRerankMaxCandidates, value, (s, v) => s.MemoryRerankMaxCandidates = v);
    }
    public int MemoryRerankCandidateContentLength {
        get => S.MemoryRerankCandidateContentLength;
        set => Set(S.MemoryRerankCandidateContentLength, value, (s, v) => s.MemoryRerankCandidateContentLength = v);
    }
    public string MemoryCategoryWeight {
        get => S.MemoryCategoryWeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Set(S.MemoryCategoryWeight, v, (s, x) => s.MemoryCategoryWeight = x); }
    }
    public string MemoryEmojiWeight {
        get => S.MemoryEmojiWeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) Set(S.MemoryEmojiWeight, v, (s, x) => s.MemoryEmojiWeight = x); }
    }
    public int MemoryMaxFactsPerCompaction {
        get => S.MemoryMaxFactsPerCompaction;
        set => Set(S.MemoryMaxFactsPerCompaction, value, (s, v) => s.MemoryMaxFactsPerCompaction = v);
    }
    public int MemoryDiaryMaxEntryLength {
        get => S.MemoryDiaryMaxEntryLength;
        set => Set(S.MemoryDiaryMaxEntryLength, value, (s, v) => s.MemoryDiaryMaxEntryLength = v);
    }

    [ObservableProperty]
    private string _promptPreview = string.Empty;

    [ObservableProperty]
    private SessionInfo? _selectedSession;

    public ObservableCollection<MessageViewModel> Messages { get; } = new();
    public ObservableCollection<SessionInfo> Sessions { get; } = new();

    /// <summary>
    /// Прикреплённые к следующему сообщению файлы (картинки и т.п.). Копируются в
    /// artifacts/msg_&lt;id&gt;/ при отправке и уходят как multimodal_data (маркер + base64),
    /// а не текстовым мусором. Текстовые файлы в этот список не попадают — их содержимое
    /// вставляется в ввод как сейчас (AttachFiles читает их как текст).
    /// </summary>

    public ObservableCollection<PendingAttachment> PendingAttachments { get; } = new();

    // ── Профили чата (config/chat-profiles.json): назначение кусков текущей сессии ───

    /// <summary>Ключи кусков профиля этой сессии; null = кусок default. Живут в SessionData.</summary>
    private string? _samplerKey;
    private string? _promptKey;
    private string? _stateBlockKey;

    /// <summary>main-сессия управляется идентичностью — настройка чата для неё закрыта.</summary>
    public bool IsMainSession => _sessions.CurrentId == MainAgent.SessionId;

    /// <summary>
    /// Редактор статичных профилей чата — ЕДИНСТВЕННОЕ место правки пресетов, живёт во
    /// вкладке «Настройки» (решение владельца: настройки не размазываются по другим вкладкам).
    /// </summary>
    public ChatProfilesEditorViewModel Profiles { get; } = new();

    /// <summary>Индекс вкладки «Настройки» в главном окне (для перехода из шестерёнки чата).</summary>
    public const int SettingsTabIndex = 2;

    [ObservableProperty]
    private int _selectedTabIndex;

    public MainViewModel() {
        // Композиционный корень главного чата: граф сервисов собирается здесь (единственное
        // место, знающее порядок). Цикла pipeline⇄stateBlocks больше нет — кэш серверных
        // фактов живёт в ServerProps, оба читают его независимо.
        _log.Changed += OnLogChanged;
        _background = new BackgroundWork(status => StatusText = status);
        TurnsPanel = new TurnPanel(_background.Turns);

        _serviceLlm = new ServiceCompletionClient(
        () => Endpoint,
        () => BuildOptions(ServiceCompletionClient.MaxTokens));
        _stateBlocks = new StateBlockBuilder(
        _log.AssignPendingIds,
        () => _log.NextMessageId,
        () => EffectiveContextSize,
        _serverProps,
        () => _log,
        () => _memorySurfacer.GetSurfacedForStateBlock(),
        () => _memorySurfacer.MemoryNag,
        () => _pairsStore.Pending);
        _pipeline = new PromptPipeline(
        () => _log,
        ResolveSystemPrompt,
        _toolRegistry,
        _serverProps,
        messages => _stateBlocks.Build(),
        ct => MultimodalContext.BuildAsync(SessionDir(), Endpoint, _serverProps, ct));

        _maintenance = new ContextMaintenance(
        _log,
        _chatState,
        _compaction,
        (user, system, onChunk, ct) => _serviceLlm.CompleteStructuredAsync(user, system, onChunk, ct),
        _layerStore,
        _memorySurfacer,
        ct => _pipeline.CountNextTokensAsync(ct),
        GetEffectiveContextSizeAsync,
        () => _sessions.CurrentId,
        new ContextBackupStore(ChatSessions.Root),
        new ContextMaintenance.Ui(
        status => StatusText = status,
        generating => IsGenerating = generating,
        SaveCurrent));

        // Интерактив инструментов (ask_user, подтверждение shell) — pull-модель: оконные
        // провайдеры живут в ChatInteraction, Core не знает про окна и FSM.
        _interaction = new ChatInteraction(_chatState);
        _interaction.Register();

        // Жизненный цикл: единая точка старта/остановки сервисов (закрытие — LIFO, без бросков).
        _lifecycle = new AppLifecycle(status => StatusText = status);

        Messages.CollectionChanged += (_, _) => {
            RerollCommand.NotifyCanExecuteChanged();
            ContinueCommand.NotifyCanExecuteChanged();
            RefreshPromptPreview();
        };

        // Вложения к следующему сообщению: SendCommand.canexec меняется (можно отправить
        // и картинку без текста) + чипсы в UI.
        PendingAttachments.CollectionChanged += (_, _) => SendCommand.NotifyCanExecuteChanged();
        EnsureMainSession();
        RestoreLastSession();
        RefreshPromptPreview();

        // Вкладка «Диагностика»: стекло в состояние FSM, бюджет контекста, сборки, память.
        Diagnostics = new DiagnosticsViewModel(
        _chatState,
        () => _serverProps.LastActualPromptTokens(_log),
        () => EffectiveContextSize,
        () => MaxTokens);

        // Вкладка «Суммаризация»: ре-прогоны и редактирование резюме/слоёв/промптов.
        Summarization = new SummarizationViewModel(RunSummarizationCallAsync);

        // Heartbeat: опрос wake/ и расписания. Период опроса фиксированный (20 с),
        // частота реальных пробуждений — HeartbeatIntervalMinutes; сигналы не ждут расписания.
        _heartbeat = new HeartbeatController(
        new WakeSignalStore(),
        isBusy: () => _chatState.IsBusy,
        heartbeatEnabled: () => HeartbeatEnabled,
        heartbeatIntervalMinutes: () => HeartbeatIntervalMinutes,
        setStatus: status => StatusText = status,
        startTurn: prompt => RunHeartbeatTurnAsync(prompt),
        flushMemory: FlushMemoryVectorsAsync,
        timer: new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) },
        watchdogGuard: WatchdogLauncher.EnsureAlive);
        _lifecycle.Register(_heartbeat);

        // Настройки: закрытие приложения — единственный синхронный flush (дебаунс не гарантирован).
        _lifecycle.Register(new DelegateAppService("настройки", shutdown: FlushSettingsSave));
        // Настройки, изменённые агентом изнутри (инструмент set_setting): живой экземпляр уже
        // обновлён и записан, остаётся перерисовать биндинг. Событие может прийти из
        // agent-потока → маришализуем на Dispatcher (см. OnSettingsChangedExternally).
        SettingsStore<AppSettings>.Changed += OnSettingsChangedExternally;
        _lifecycle.StartAll();

        // Саморебилд индикатор v2
        var lastBuild = StateBlockBuilder.LastBuild();

        if (lastBuild is not null) {
            StatusText = $"⚡ Сборка {lastBuild.Id} | Режим бога активирован";
        }

        // Контекст краха: каждая запись CrashLog несёт «что делалось в момент смерти»
        // (активные ходы, сессия, FSM) — картину не придётся собирать по кускам.
        CrashLog.AddContextProvider(BuildCrashContext);

        // Предыдущий запуск закончился крахом — не даём проскочить незаметно.
        if (File.Exists(CrashLog.LastCrashFile) &&
            (DateTime.Now - File.GetLastWriteTime(CrashLog.LastCrashFile)).TotalHours < 24) {
            StatusText = "⚠ предыдущий запуск закончился крахом — logs/last-crash.log";
        }
    }

    /// <summary>Снимок «что происходило» для записей CrashLog (вызывается синхронно, без блокировок).</summary>
    private string BuildCrashContext() {
        var sb = new StringBuilder();
        sb.AppendLine($"session: {_sessions.CurrentId}");
        sb.AppendLine($"chat FSM: {_chatState.Current}; generating: {IsGenerating}");
        // QwenPlayground.Core.Runtime.TurnState: вложенный класс TurnState хода затеняет имя.
        var active = _background.Turns.Turns
            .Where(t => t.State is QwenPlayground.Core.Runtime.TurnState.Queued
                or QwenPlayground.Core.Runtime.TurnState.Running)
            .ToList();
        if (active.Count == 0) {
            sb.AppendLine("active turns: none");
        }
        else {
            sb.AppendLine("active turns:");
            foreach (var turn in active) {
                sb.AppendLine($"  - {turn.Name}: {turn.State}");
                foreach (var line in turn.Journal.TakeLast(5)) {
                    sb.AppendLine($"      {line}");
                }
                if (turn.Error is not null) {
                    sb.AppendLine($"      error: {turn.Error}");
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>Централизованная остановка сервисов при закрытии (LIFO, ошибки собираются).</summary>
    public List<string> Shutdown() {
        return _lifecycle.ShutdownAll();
    }

    public void AnnounceRestarts() {
        if (!SelfBuildPaths.TryGetDeployedRunRoot(out var runRoot)) 
            return;        

        var unannounced = BuildJournal.Load(runRoot)
        .Where(e => e is { Announced: false, Status: "success" or "failed" })
        .ToList();
        if (unannounced.Count == 0) 
            return;        

        foreach (var entry in unannounced) {
            var outcome = entry.Status == "success"
            ? $"[перезапуск] сборка {entry.Id} успешно запущена."
            : $"[перезапуск] сборка {entry.Id} провалилась ({entry.FailureReason}). Выполнен откат на предыдущую версию.";
            if (_log.Count > 0 && _log[^1].Role == ChatRole.Tool) {
                _log[^1].Content += "\n" + outcome;
            }

            else {
                _log.Add(ChatMessage.Tool(outcome));
            }
        }

        BuildJournal.MarkAnnounced(runRoot, unannounced.Select(e => e.Id));

        RebuildMessageViews();
        SaveCurrent();
    }
    public void ResumePendingChain() {
        AnnounceRestarts();
        // Гвард по FSM, а не по IsGenerating: при Compacting/Awaiting* второй ход
        // бросил бы InvalidOperationException внутри Transition.

        if (!_chatState.IsBusy && _log.Count > 0 && _log[^1].Role == ChatRole.Tool) {
            _background.Queue("продолжение цепочки tool", () => GenerateAsync());
        }
    }

    /// <summary>
    /// Один изолированный LLM-вызов для вкладки «Суммаризация»: промпт рендерится
    /// через submit_result, токены стримятся наружу (onToken), результат вытаскивается
    /// структурно. Те же эндпоинт и семплер, что в компакции; трафик — в TrafficLog.
    /// </summary>
    private async Task<string> RunSummarizationCallAsync(
    string userContent, string? system, Action<string>? onToken, CancellationToken cancellationToken) {
        var result = await _serviceLlm.CompleteStructuredAsync(userContent, system, onToken, cancellationToken);
        return result ?? string.Empty;
    }

    /// <summary>
    /// Сессия main-агента — сессия по умолчанию: грузим её из sessions/main/chat.json,
    /// иначе создаём. Идентичность (main-agent.md) и слои памяти в историю не пишутся —
    /// они собираются в системный промпт при каждом рендере (InjectedIdentity).
    /// </summary>
    private void EnsureMainSession() {
        var data = _sessions.EnsureMain();

        if (data is not null) {
            _log.ReplaceAll(StripBakedSystem(data.Messages));
            _log.SetNextMessageId(data.NextMessageId);
        }
        else {
            _log.Clear();
        }
        SaveCurrent();
    }

    /// <summary>
    /// У старой main-сессии system-сообщение — запечённая идентичность (+старое резюме).
    /// Теперь идентичность собирается динамически, поэтому снимаем её из истории.
    /// </summary>
    private static List<ChatMessage> StripBakedSystem(IReadOnlyList<ChatMessage> messages) =>
    messages.Count > 0 && messages[0].Role == ChatRole.System ? messages.Skip(1).ToList() : messages.ToList();

    /// <summary>
    /// Flush-векторизация памяти: факты без слоёв или со старой LayersVersion классифицируются
    /// на компаньон-модели на фоне (троттлинг + бюджет за проход), чтобы не забивать поток чата.
    /// Ошибки глотаются — не критичный путь; следующее сердцебиение повторит попытку.
    /// </summary>
    private async Task FlushMemoryVectorsAsync() {
        if (_memoryFlushInFlight || _chatState.IsBusy) 
            return;
        
        if (DateTime.UtcNow - _lastMemoryFlushAt < MemoryFlushInterval) 
            return;
        
        _lastMemoryFlushAt = DateTime.UtcNow;
        _memoryFlushInFlight = true;

        try {
            var endpoint = CompanionEndpoint;
            var token = _cancellation?.Token ?? CancellationToken.None;
            var store = new MemoryStore();
            var processed = await MemoryClassifier.FlushAsync(store, endpoint, AppSettings.Get().MemoryFlushBudget, token);

            if (processed > 0) {
                StatusText = $"🧠 память: векторизовано {processed} фактов";
            }

            else {
                // Все факты с векторами — надмозг переключается на поиск дубликатов.
                var scan = await MemorySimilarity.ScanPassAsync(
                store, _pairsStore, endpoint, AppSettings.Get().MemoryScanProbeBudget,
                (prompt, _, _, ct) => LlmProbeClient.ProbeAsync(endpoint, prompt, nProbs: 20, ct),
                token);

                var pending = new PairsStore(store.Root).Pending.Count;

                if (scan.QueuedSimilar > 0) {
                    StatusText = $"🧠 память: {scan.QueuedSimilar} похожих фактов ждут разрешения (memory_manage)";
                }

                else if (pending > 0 && scan.Probes > 0) {
                    StatusText = $"🧠 память: скан дубликатов, пар в очереди: {pending}";
                }
            }
        }

        catch (OperationCanceledException) {

        }

        catch {
            // flush — не критичный путь, повторится на следующем тике
        }

        finally {
            _memoryFlushInFlight = false;
        }
    }

    /// <summary>Ручной wake (кнопка): если есть сигнал — обработать его, иначе обычный heartbeat.</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void WakeNow() => _heartbeat.WakeNow();

    /// <summary>Ход main-агента по инициативе приложения: всегда агентный режим (иначе бессмысленно).</summary>
    private async Task RunHeartbeatTurnAsync(string prompt) {
        var userMessage = ChatMessage.User(prompt);
        _log.Add(userMessage);
        Messages.Add(MessageViewModel.FromMessage("user", userMessage));
        await GenerateWithBudgetAsync(continueLastAssistant: false);
        SaveCurrent();
    }

    partial void OnSelectedSessionChanged(SessionInfo? value) {
        if (value is null || value.Id == _sessions.CurrentId || IsGenerating) 
            return;
        
        LoadSession(value.Id);
    }

    [RelayCommand]
    private void NewSession() {
        if (IsGenerating) 
            return;        

        if (_log.Count > 0) 
            SaveCurrent();        

        _log.Clear();
        _sessions.StartNew();
        _samplerKey = null;
        _promptKey = null;
        _stateBlockKey = null;
        OnPropertyChanged(nameof(IsMainSession));
        RefreshSessions();
        SelectedSession = null;
        RefreshPromptPreview();
    }    
    private CancellationTokenSource? _settingsSaveDebounce;
    /// <summary>
    /// Отложенная запись настроек на диск: правки полей в UI идут пачками (каждое нажатие
    /// стрелки в numeric-поле — событие), писать на каждый чанг незачем. 800 мс тишины — пишем.
    /// </summary>
    private void ScheduleSettingsSave() {
        _settingsSaveDebounce?.Cancel();
        _settingsSaveDebounce?.Dispose();
        _settingsSaveDebounce = new CancellationTokenSource();
        var token = _settingsSaveDebounce.Token;
        _background.Queue("сохранение настроек", async () => {
            await Task.Delay(800, token);
            AppSettings.Save();
        });
    }

    /// <summary>
    /// Настройки изменились извне (инструмент set_setting агента): живой экземпляр уже обновлён
    /// и записан на диск, остаётся перерисовать биндинг. Событие приходит из agent-потока —
    /// WPF-биндинг требует уведомлений на UI-потоке, поэтому маришализуем на Dispatcher.
    /// </summary>
    private void OnSettingsChangedExternally(AppSettings _) {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) {
            RefreshSettingsViews();
        } else {
            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(RefreshSettingsViews));
        }
    }

    /// <summary>
    /// Перерисовать биндинг настроек после внешнего изменения. Тонкие виды читают живой
    /// AppSettings, поэтому достаточно сообщить биндингу, что соответствующие свойства могли
    /// измениться. Собираем рефлексией по совпадению имени с полем AppSettings: новое поле
    /// настроек подхватится автоматически, без хрупкого ручного списка.
    /// </summary>
    private void RefreshSettingsViews() {
        var settingsType = typeof(AppSettings);
        foreach (var property in typeof(MainViewModel)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.CanWrite
                                 && settingsType.GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance) is not null)) {
            OnPropertyChanged(property.Name);
        }
        OnPropertyChanged(nameof(ReasoningEffortIndex));
        RefreshPromptPreview();
    }

    [RelayCommand]
    private void DeleteSession() {
        if (IsGenerating || SelectedSession is null) 
            return;
        
        if(SelectedSession.Id == MainAgent.SessionId){
            StatusText = "основную сессию нельзя удалить";
            return;
        }

        if (_sessions.Delete(SelectedSession.Id)) {
            // Удалили текущую: ChatSessions уже переключился на свежую пустую.
            _log.Clear();
        }

        RefreshSessions();
        RefreshPromptPreview();
        _sessions.PersistCurrentId();
    }

    private bool LoadSession(string id) {
        var data = _sessions.Load(id);
        if (data is null) 
            return false;        

        _log.ReplaceAll(id == MainAgent.SessionId ? StripBakedSystem(data.Messages) : data.Messages);
        _log.SetNextMessageId(data.NextMessageId);
        _samplerKey = data.SamplerKey;
        _promptKey = data.PromptKey;
        _stateBlockKey = data.StateBlockKey;
        OnPropertyChanged(nameof(IsMainSession));
        StatusText = string.Empty;
        RefreshPromptPreview();
        return true;
    }

    // ── Профили чата: резолверы хода и диалог настройки (шестерёнка) ────────────────

    /// <summary>
    /// Единый с превью и ходом источник системного промпта: main-сессия — динамическая
    /// идентичность, специализированная — кусок-промпт из статичного хранилища профилей.
    /// </summary>
    private string? ResolveSystemPrompt() {
        if (_sessions.CurrentId == MainAgent.SessionId) {
            return _identity.GetFor(true);
        }
        return ChatProfiles.Get().ResolvePrompt(_promptKey).RenderSystemPrompt();
    }

    /// <summary>
    /// Белый список инструментов куска-промпта; пусто — полный реестр. Неизвестные имена
    /// в списке молча не находятся — это конфиг, а не ошибка хода.
    /// </summary>
    private IReadOnlyList<ToolDefinition>? RestrictedTools(IReadOnlyList<string> allowed)
    {
        if (allowed.Count == 0)
        {
            return null;
        }
        var allow = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _toolRegistry.Definitions.Where(d => allow.Contains(d.Name)).ToList();
    }

    /// <summary>Усилие размышления из профиля («XHigh»/«Medium»/«Low»); пустое/мусорное — из настроек.</summary>
    private static ReasoningEffort? ParseEffort(string text) =>
        !string.IsNullOrWhiteSpace(text) && Enum.TryParse<ReasoningEffort>(text.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Шестерёнка у панели сессий: назначить куски профиля текущему чату. main-агент
    /// настраивается идентичностью и общими правилами — диалог для него закрыт.
    /// </summary>
    [RelayCommand]
    private void OpenChatTuning() {
        if (IsMainSession || IsGenerating)
            return;
        var profiles = ChatProfiles.Get();
        var dialog = new ChatTuningDialog(
            OrderedKeys(profiles.Samplers.Keys),
            OrderedKeys(profiles.Prompts.Keys),
            OrderedKeys(profiles.StateBlocks.Keys),
            _samplerKey, _promptKey, _stateBlockKey)
        { Owner = System.Windows.Application.Current.MainWindow };
        // Пресеты редактируются в ЕДИНСТВЕННОМ месте — вкладка «Настройки»; диалог закрываем.
        dialog.GoToSettings = () => SelectedTabIndex = SettingsTabIndex;
        if (dialog.ShowDialog() == true) {
            _samplerKey = dialog.SelectedSamplerKey;
            _promptKey = dialog.SelectedPromptKey;
            _stateBlockKey = dialog.SelectedStateBlockKey;
            SaveCurrent(); // редкое событие — пишем сразу, выбор не теряется при закрытии
            RefreshPromptPreview();
            StatusText = "Настройка чата применена: действует со следующего хода.";
        }
    }

    private static List<string> OrderedKeys(IEnumerable<string> keys) =>
        keys.OrderBy(k => k == ChatProfileSet.DefaultKey ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Восстановить последнюю открытую сессию (из settings.json). Если её нет, она равна
    /// main или была удалена — остаёмся на main-агенте (дефолт).
    /// </summary>
    private void RestoreLastSession() {
        var lastId = S.LastSessionId ?? _sessions.LastOpenedId;
        if (string.IsNullOrEmpty(lastId) || lastId == _sessions.CurrentId) 
            return;        
        
        if (!LoadSession(lastId)) 
            _sessions.PersistCurrentId(); // последняя сессия пропала — фиксируем main, чтобы не пытаться снова        
    }

    public void SaveCurrent() {
        _log.AssignPendingIds();
        _sessions.SaveCurrent(_log, _log.NextMessageId,
            samplerKey: _samplerKey, promptKey: _promptKey, stateBlockKey: _stateBlockKey);
        RefreshSessions();
    }

    private void RefreshSessions() {
        _sessions.RefreshList();
        Sessions.Clear();
        foreach (var info in _sessions.List) {
            Sessions.Add(info);
        }

        SelectedSession = Sessions.FirstOrDefault(s => s.Id == _sessions.CurrentId);
    }

    private void RebuildMessageViews() {
        var sessionDir = SessionDir();
        Messages.Clear();
        foreach (var message in _log) {
            var view = MessageViewModel.FromMessage(RoleName(message), message);
            view.LoadArtifacts(sessionDir);
            Messages.Add(view);
        }
    }

    private static string RoleName(ChatMessage message) => message.Role.ToString().ToLowerInvariant();
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync() {
        var text = InputText.Trim();
        InputText = string.Empty;
        var userMessage = ChatMessage.User(text);
        _log.Add(userMessage); // ID присваивается здесь же — до копирования вложений
        var userView = MessageViewModel.FromMessage("user", userMessage);
        Messages.Add(userView);
        var attachments = PendingAttachments.ToList();
        PendingAttachments.Clear();
        var metaStore = new MessageMetaStore(SessionDir());
        var failedAttachments = new List<string>();

        foreach (var attachment in attachments) {
            try {
                metaStore.AddArtifact(userMessage.Id, attachment.FullPath);
            }
            catch {
                // файл не прочитался — пропускаем вложение, но сообщаем: иначе картинка
                // молча не доедет до модели и ход пройдёт «вслепую»
                failedAttachments.Add(attachment.Name);
            }
        }

        if (failedAttachments.Count > 0) {
            StatusText = $"вложение не прикреплено: {string.Join(", ", failedAttachments)}";
        }

        userView.LoadArtifacts(SessionDir());
        await GenerateAsync();
        SaveCurrent();
    }

    private bool CanSend() => !IsBusy && (InputText.Trim().Length > 0 || PendingAttachments.Count > 0) && Endpoint.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(IsGenerating))]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void Clear() {
        _log.Clear();
        StatusText = string.Empty;
        SaveCurrent();
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void Rollback(MessageViewModel? message) {
        if (message is null) 
            return;        

        var index = Messages.IndexOf(message);

        if (index < 0) 
            return;        

        while (Messages.Count > index) {
            Messages.RemoveAt(Messages.Count - 1);
        }

        _log.RemoveFrom(index);
        SaveCurrent();

    }

    [RelayCommand]
    private void InspectPrompt(MessageViewModel? message) {
        var text = message?.GetInspectionText() ?? "(нет данных генерации)";
        new Views.PromptWindow(text) { Owner = System.Windows.Application.Current.MainWindow }.Show();
    }

    [RelayCommand]
    private void EditMessage(MessageViewModel? message) {
        if (IsGenerating || message?.Source is null) 
            return;        

        new Views.EditMessageWindow(message, OnMessageEdited) {
            Owner = System.Windows.Application.Current.MainWindow
        }.ShowDialog();
    }

    private void OnMessageEdited() {
        RefreshPromptPreview();
        SaveCurrent();
    }

    [RelayCommand]
    private void CopyMessage(MessageViewModel? message) {
        if (message is null) return;
        var sb = new StringBuilder();
        if (message.Reasoning.Length > 0) {
            sb.Append("[мысли]\n").Append(message.Reasoning).Append('\n');
        }
        foreach (var tc in message.ToolCalls) {
            sb.Append(tc).Append('\n');
        }
        sb.Append(message.Content);
        System.Windows.Clipboard.SetText(sb.ToString());
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void CopyChat() {
        var builder = new StringBuilder();
        foreach (var message in Messages) {
            builder.Append("### ").Append(message.Role).Append('\n');
            if (message.Reasoning.Length > 0) {
                builder.Append("[reasoning]\n").Append(message.Reasoning).Append('\n');
            }
            if (message.Content.Length > 0) {
                builder.Append(message.Content).Append('\n');
            }
            foreach (var call in message.ToolCalls) {
                builder.Append("[tool call] ").Append(call).Append('\n');
            }
            builder.Append('\n');
        }
        System.Windows.Clipboard.SetText(builder.ToString());
        StatusText = "чат скопирован в буфер обмена";
    }
    [RelayCommand(CanExecute = nameof(CanReroll))]
    private async Task RerollAsync(MessageViewModel? message) {
        if (message is null) 
            return;
        
        Rollback(message);
        await GenerateAsync();
        SaveCurrent();
    }
    private bool CanReroll(MessageViewModel? message) =>
    !IsGenerating && message is not null && Messages.Count > 0 &&
    ReferenceEquals(message, Messages[^1]) && message.Role == "assistant";
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task ContinueAsync() {
        await GenerateAsync(continueLastAssistant: true);
        SaveCurrent();
    }
    private bool CanContinue() =>
    !IsGenerating && Messages.Count > 0 && Messages[^1].Role == "assistant";
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void AttachFiles() {
        var dialog = new Microsoft.Win32.OpenFileDialog {
            Multiselect = true,
            Title = "Прикрепить файлы"
        };
        if (dialog.ShowDialog() != true) 
            return;
        
        var builder = new StringBuilder(InputText);
        foreach (var file in dialog.FileNames) {
            // Картинки (и прочие бинарники) в текст не читаем — это даёт мусор в сообщении.
            // Их кладём во вложения: при отправке копируются в artifacts/msg_<id>/ и уходят
            // как multimodal_data (маркер + base64), модель их увидит как изображение.
            if (IsBinaryFile(file)) {
                PendingAttachments.Add(new PendingAttachment(Path.GetFileName(file), file));
                continue;
            }
            string content;
            try {
                content = File.ReadAllText(file);
            }
            catch {
                continue;
            }
            const int cap = 20000;
            if (content.Length > cap) {
                content = content[..cap] + "\n... (обрезано)";
            }
            if (builder.Length > 0) {
                builder.Append('\n');
            }
            builder.Append("[файл: ").Append(Path.GetFileName(file)).Append("]\n").Append(content).Append('\n');
        }
        InputText = builder.ToString();
    }
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void RemoveAttachment(PendingAttachment? attachment) {
        if (attachment is not null) {
            PendingAttachments.Remove(attachment);
        }
    }
    /// <summary>Вставить картинку из буфера обмена во вложения (без текста).</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void PasteImage() {
        if (!System.Windows.Clipboard.ContainsImage()) {
            StatusText = "в буфере обмена нет картинки";
            return;
        }
        try {
            var image = System.Windows.Clipboard.GetImage();
            if (image is null) 
                return;
            
            var dir = Path.Combine(Path.GetTempPath(), "qwen-paste");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"paste-{DateTime.Now:yyyyMMdd-HHmmssfff}.png");
            using (var stream = File.Create(file)) {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                encoder.Save(stream);
            }
            PendingAttachments.Add(new PendingAttachment(Path.GetFileName(file), file));
            StatusText = "картинка из буфера добавлена во вложения";
        }
        catch {
            StatusText = "не удалось вставить картинку из буфера";
        }
    }
    /// <summary>Открыть прикреплённый файл системным просмотрщиком.</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void OpenAttachment(MessageAttachment? attachment) {
        if (attachment is null || !File.Exists(attachment.FullPath)) 
            return;
        
        try {
            Process.Start(new ProcessStartInfo(attachment.FullPath) { UseShellExecute = true });
        }
        catch {
            // просмотрщик не открылся — профилактика
        }
    }
    private static readonly HashSet<string> BinaryExtensions =
    new(StringComparer.OrdinalIgnoreCase)
    {
".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".ico", ".svg",
".pdf", ".mp4", ".mp3", ".wav", ".zip", ".7z", ".rar", ".exe", ".bin"
    };
    /// <summary>Бинарные файлы (картинки/документы/архивы) как текст не читаются — во вложения.</summary>
    private static bool IsBinaryFile(string path) {
        var ext = Path.GetExtension(path);
        return BinaryExtensions.Contains(ext);
    }
    /// <summary>
    /// Синхронный flush настроек при закрытии. Дебаунс (800 мс) при выключении приложения
    /// не гарантирован: отложенный таск может быть отменён или не успеть выполниться до
    /// завершения процесса — настройки терялись, на старте грузился дефолт.
    /// </summary>
    public void FlushSettingsSave() {
        _settingsSaveDebounce?.Cancel();
        AppSettings.Save();
    }

    /// <summary>Ручная компакция из UI.</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task CompactAsync() => await _maintenance.CompactFromUiAsync();
    private bool CanInteract() => !IsBusy;
    private GenerationOptions BuildOptions(int? maxTokensOverride = null) =>
    S.ToGenerationOptions(maxTokensOverride);
    /// <summary>
    /// Опции для сервисных вызовов (суммаризация, слои L1/L2/L3, извлечение фактов).
    /// Макс. бюджет генерации — большой: резюме может быть небольшим, но размышления о нём
    /// способны съесть десятки тысяч токенов, и мы не должны обрезать модель на середине мысли
    /// (иначе submit_result никогда не будет вызван). ReasoningEffort.Medium (без инструкции)
    /// задаётся на уровне рендера шаблона (StructuredCompletion.Render), здесь только бюджет.
    /// </summary>
    private GenerationOptions BuildServiceOptions() => BuildOptions(ServiceMaxTokens);
    /// <summary>Потолок генерации одного сервисного вызова — ждём максимум (весь остаток контекста на размышления).</summary>
    private const int ServiceMaxTokens = 60000;
    /// <summary>
    /// State-блок для модели (сборка — в <see cref="StateBlockBuilder"/>): после реального
    /// рендера счётчик показов всплывших памятей сдвигается.
    /// </summary>
    private StateBlock BuildStateBlock(IReadOnlyList<ChatMessage> conversation) {
        var state = _stateBlocks.Build();
        _memorySurfacer.OnRendered();
        return state;
    }
    /// <summary>
    /// Свойства сервера (media_marker + n_ctx) — кэш в <see cref="ServerProps"/> на TTL.
    /// Endpoint передаётся на каждый вызов: пользователь может его сменить.
    /// </summary>
    private Task FetchServerPropsAsync(CancellationToken ct = default) =>
    _serverProps.FetchAsync(Endpoint, ct);
    /// <summary>
    /// <summary>Однострочное содержимое для state-блока: новые строки → пробелы, обрезка.</summary>
    private static string ToSingleLine(string text, int maxLength) {
        var oneLine = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= maxLength ? oneLine : oneLine[..maxLength] + "…";
    }
    /// <summary>
    /// Эффективный размер окна: реальный n_ctx сервера (если известен), иначе настроенный
    /// ContextSize. Для проверки «влезет ли» сравниваем именно с ним — это то, что реально
    /// спрашивает сервер.
    /// </summary>
    private int EffectiveContextSize => Math.Min(ContextSize, _serverProps.NContext ?? ContextSize);
    private void RefreshPromptPreview() {
        try {
            var preview = _pipeline.RenderForPreview();
            PromptPreview = preview.Length == 0 ? "(пусто)" : preview;
        }
        catch (Exception exception) {
            PromptPreview = $"[не удалось отрендерить: {exception.Message}]";
        }
    }
    private Task GenerateAsync(bool continueLastAssistant = false) =>
    GenerateWithBudgetAsync(continueLastAssistant);
    private async Task GenerateWithBudgetAsync(bool continueLastAssistant) {
        // Бюджет-проверка идёт ДО try/catch в GenerateCoreAsync и до перевода FSM: падение здесь
        // (сервер недоступен, /tokenize не вернул точное число) раньше оставляло ход в тишине —
        // fire-and-forget задача (heartbeat/wake) гасла без следа, а добавленное user-сообщение
        // «висело» несохранённым. Показываем ошибку и сохраняем историю.
        if (!continueLastAssistant) {
            try {
                await _maintenance.EnsureBudgetAsync(CancellationToken.None);
            }
            catch (OperationCanceledException) {
                throw;
            }
            catch (Exception exception) {
                StatusText = $"ошибка проверки бюджета контекста: {exception.Message}";
                SaveCurrent();
                return;
            }
        }
        // Режим всегда агентный (тумблер режимов убран из UI, 2026-08-22): инструменты
        // доступны, если задан проект.
        var agentic = ProjectRoot.Trim().Length > 0;
        if (agentic) {
            Directory.CreateDirectory(ProjectRoot);
        }
        await GenerateCoreAsync(agentic, continueLastAssistant);
    }
    /// <summary>Реальный n_ctx сервера (кэш), иначе настроенный ContextSize.</summary>
    private async Task<int> GetEffectiveContextSizeAsync() {
        await FetchServerPropsAsync();
        return EffectiveContextSize;
    }
    /// <summary>
    /// Единый путь генерации (всегда агентный: тумблер режимов убран 2026-08-22).
    /// allowToolExecution/toolDefinitions зависят от того, задан ли ProjectRoot:
    /// без проекта ход идёт как обычный чат — инструменты не рекламируются и не выполняются.
    /// </summary>
    private async Task GenerateCoreAsync(bool agentic, bool continueLastAssistant) {
        var continued = continueLastAssistant && _log.Count > 0 &&
        _log[^1].Role == ChatRole.Assistant
        ? _log[^1]
        : null;
        // Состояние одного хода: локальные мутации обработчиков событий собраны вместе.
        var turn = new TurnState { Continued = continued, Agentic = agentic };
        if (continued is not null) {
            turn.CurrentAssistant = Messages[^1];
            turn.Raw.Append(continued.ToRawOutput());
        }
        // FSM: Idle → Generating
        _chatState.Transition(ChatState.Generating);
        IsGenerating = true;
        _cancellation = new CancellationTokenSource();
        try {
            var loop = new AgentLoop(_toolRegistry);
            var multimodal = await MultimodalContext.BuildAsync(SessionDir(), Endpoint, _serverProps, _cancellation.Token);
            // Профиль чата: три независимых куска из статичного хранилища (default = как раньше).
            // main-агент ведётся идентичностью — промпт-кусок и отключение state-блока на него не действуют.
            var isMain = _sessions.CurrentId == MainAgent.SessionId;
            var profiles = ChatProfiles.Get();
            var sampler = profiles.ResolveSampler(_samplerKey);
            var prompt = profiles.ResolvePrompt(_promptKey);
            var stateEnabled = isMain || profiles.ResolveStateBlock(_stateBlockKey).Enabled;
            var toolsAllowed = agentic && (isMain || prompt.Tools);
            await foreach (var agentEvent in loop.RunAsync(new AgentLoopRequest {
                Conversation = _log,
                OnFactSaved = item => _memorySurfacer.SurfaceOwnWrite(item.Id, item.Content),
                ContinueLastAssistant = continued is not null,
                AllowToolExecution = toolsAllowed,
                ToolDefinitions = toolsAllowed ? RestrictedTools(prompt.AllowedTools) : Array.Empty<ToolDefinition>(),
                Generation = S.ToGenerationOptions(sampler),
                MaxIterations = S.ResolveMaxIterations(sampler),
                // Nag самопроверки живёт ВНУТРИ state-блока — без блока nag'ать некуда.
                SanityCheckInterval = stateEnabled ? S.ResolveSanityCheckInterval(sampler) : 0,
                ReasoningEffort = ParseEffort(prompt.ReasoningEffort),
                StateProvider = stateEnabled ? BuildStateBlock : null,
                SystemPromptProvider = _ => ResolveSystemPrompt(),
                ToolExecutor = async (name, args, ctx, ct) => {
                    // Менеджмент памяти сбрасывает mem_nag: модель задела memory_* — значит занималась.
                    if (name.StartsWith("memory_", StringComparison.Ordinal)) {
                        _memorySurfacer.OnMemoryToolUsed();
                    }
                    return await _toolRegistry.ExecuteDetailedAsync(name, args, ctx, ct);
                },
                // FSM: Generating → Compacting → Generating (между итерациями). Точный размер
                // промпта — у сервера (/tokenize); решение «сжимать» и само сжатие — в ContextMaintenance.
                ContextBudgetGuard = ct => _maintenance.EnsureBudgetAsync(ct),
                Multimodal = multimodal,
                SessionDir = SessionDir(),
                CancellationToken = _cancellation.Token
            })) {
                DispatchEvent(turn, agentEvent);
            }
        }
        catch (OperationCanceledException) {
            // Хвост стрима мог не успеть опубликоваться (троттлинг) — финализируем вид до разбора.
            turn.CurrentAssistant?.FlushStreaming();
            CommitCanceledPartial(turn.Continued, turn.CurrentAssistant, turn.Raw.ToString());
        }
        catch (Exception exception) {
            // В single-режиме исторически показываем ошибку прямо в пузыре ответа.
            if (!agentic && turn.CurrentAssistant is not null) {
                turn.CurrentAssistant.Content = $"[ошибка] {exception.Message}";
            }
            else {
                StatusText = $"ошибка: {exception.Message}";
            }
        }
        finally {
            _cancellation.Dispose();
            _cancellation = null;
            // FSM: Generating → Idle (если ещё не в RestartPending).
            // Сначала FSM, потом IsGenerating=false: уведомление CanExecuteChanged должно
            // стрельнуть, когда IsBusy уже false, иначе кнопка отката останется серой.
            if (_chatState.Current == ChatState.Generating) {
                _chatState.Transition(ChatState.Idle);
            }
            IsGenerating = false;
        }
        if (agentic && SelfBuildService.ConsumeRestartRequest() is { } restartBuildId) {
            RestartInto(restartBuildId);
        }
    }
    /// <summary>Состояние одного хода генерации: мутации обработчиков событий собраны здесь.</summary>
    private sealed class TurnState {
        /// <summary>Накопленный сырой вывод (для парсера при отмене и live-реколл).</summary>
        public StringBuilder Raw { get; } = new();
        public MessageViewModel? CurrentAssistant { get; set; }
        public MessageViewModel? PendingTool { get; set; }
        public TokenUsage? Usage { get; set; }
        public ChatMessage? Continued { get; init; }
        public bool Agentic { get; init; }
        /// <summary>BeginStreaming вызван для CurrentAssistant (continue-ход: задан заранее).</summary>
        public bool StreamStarted;
    }
    /// <summary>
    /// Диспетчер событий цикла в состояние хода и вид чата. Новый тип события —
    /// новый case + приватный обработчик; доменная логика остаётся в AgentLoop,
    /// здесь — только перевод в видимое.
    /// </summary>
    private void DispatchEvent(TurnState turn, AgentEvent agentEvent) {
        switch (agentEvent) {
            case TokenEvent token:
                OnToken(turn, token.Text);
                break;
            case AssistantMessageEvent assistant:
                OnAssistantMessage(turn, assistant.Message);
                break;
            case ToolCallStartedEvent started:
                OnToolStarted(turn, started.Name, started.Arguments);
                break;
            case ToolCallFinishedEvent finished:
                OnToolFinished(turn, finished.ToolMessage, finished.Result);
                break;
            case AgentErrorEvent error:
                StatusText = error.Message;
                break;
            case RestartPendingEvent:
                StatusText = "перезапуск в новую версию...";
                break;
            case NagEvent nag:
                Messages.Add(new MessageViewModel { Role = "user", Content = nag.Text });
                break;
        }
    }
    private void OnToken(TurnState turn, string text) {
        if (turn.CurrentAssistant is null) {
            // Новый стрим: сброс live-реколл окна.
            _memorySurfacer.ResetLiveWindow();
            turn.CurrentAssistant = AddAssistantView();
            turn.CurrentAssistant.BeginStreaming(turn.Raw.ToString());
            turn.StreamStarted = true;
        }
        else if (!turn.StreamStarted) {
            // Continue-ход: CurrentAssistant задан в GenerateCoreAsync, но BeginStreaming
            // не вызывался — _streamActive=false, все чанки молча терялись до ApplyParsed.
            turn.CurrentAssistant.BeginStreaming(turn.Raw.ToString());
            turn.StreamStarted = true;
        }
        turn.Raw.Append(text);
        turn.CurrentAssistant.AppendStreamChunk(text);
        _memorySurfacer.MaybeFireLiveRecall(turn.Agentic, text, turn.Raw, turn.Continued is not null,
        _log, _sessions.CurrentId == MainAgent.SessionId,
        CompanionEndpoint, _cancellation?.Token ?? CancellationToken.None);
    }
    private void OnAssistantMessage(TurnState turn, ChatMessage message) {
        turn.CurrentAssistant ??= AddAssistantView();
        turn.CurrentAssistant.ApplyParsed(message);
        if (message.Generation is { } generation) {
            turn.Usage = new TokenUsage(generation.PromptTokens, generation.CompletionTokens);
        }
        UpdateStatus(turn.Usage);
        turn.CurrentAssistant = null;
        turn.StreamStarted = false;
        turn.Raw.Clear();
        // Ассоциативный реколл: факты подтягиваются между итерациями, фоном на компаньон-модели.
        if (turn.Agentic) {
            var conversation = _log;
            var companion = CompanionEndpoint;
            var token = _cancellation?.Token ?? CancellationToken.None;
            _background.Queue("реколл памяти", () =>
            _memorySurfacer.RecallAfterTurnAsync(
            conversation, _sessions.CurrentId == MainAgent.SessionId, companion, token));
        }
    }
    private void OnToolStarted(TurnState turn, string name, JsonObject arguments) {
        turn.PendingTool = new MessageViewModel { Role = "tool", Content = "выполняется..." };
        turn.PendingTool.ToolCalls.Add(MessageViewModel.FormatToolCall(name, arguments));
        turn.PendingTool.ToolCallCount = 1;
        Messages.Add(turn.PendingTool);
    }
    private void OnToolFinished(TurnState turn, ChatMessage toolMessage, string result) {
        if (turn.PendingTool is null) {
            return;
        }
        turn.PendingTool.Content = result;
        // Привязываем фоновое ChatMessage и подгружаем вложения:
        // load_image в FinalizeAsync кладёт файлы в msg_<id> уже после
        // добавления tool-сообщения, поэтому их надо читать по Source.Id.
        turn.PendingTool.Source = toolMessage;
        turn.PendingTool.LoadArtifacts(SessionDir());
        turn.PendingTool = null;
    }
    private void RestartInto(string buildId) {
        SaveCurrent();
        // Launcher в pointer-режиме (pid + buildId): current.txt = buildId, старт из run/<id>.
        // Старые версии приложения передают только pid — Launcher тогда работает в legacy-режиме.
        var launcher = Path.Combine(SelfBuildPaths.LauncherDir, "QwenPlayground.Launcher.exe");
        Process.Start(new ProcessStartInfo {
            FileName = launcher,
            Arguments = $"{Environment.ProcessId} {buildId}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        System.Windows.Application.Current.Shutdown();
    }
    private void CommitCanceledPartial(ChatMessage? continued, MessageViewModel? currentAssistant, string raw) {
        if (currentAssistant is null || raw.Trim().Length == 0) {
            return;
        }
        var partial = QwenOutputParser.ParseAssistant(raw);
        partial.ToolCalls = null;
        partial.Generation = null;
        if (continued is not null) {
            continued.Reasoning = partial.Reasoning;
            continued.Content = partial.Content;
            continued.ToolCalls = null;
            continued.ThinkingClosed = partial.ThinkingClosed;
            continued.Generation = null;
            currentAssistant.ApplyParsed(continued);
        }
        else if (_log.Count > 0 && _log[^1].Role == ChatRole.Assistant) {
            currentAssistant.Source = _log[^1];
        }
        else {
            _log.Add(partial);
            currentAssistant.ApplyParsed(partial);
        }
    }
    private MessageViewModel AddAssistantView() {
        var view = new MessageViewModel { Role = "assistant" };
        Messages.Add(view);
        return view;
    }
    private void UpdateStatus(TokenUsage? usage) {
        if (usage?.PromptTokens is { } promptTokens) 
            StatusText = $"контекст: {promptTokens} токенов (+{usage.CompletionTokens?.ToString() ?? "?"})";        
    } 
}