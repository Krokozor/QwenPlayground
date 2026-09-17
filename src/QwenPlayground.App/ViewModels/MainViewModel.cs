using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.App.Desktop;
using QwenPlayground.App.Tools;
using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Heartbeat;
using QwenPlayground.Core.Mcp;
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
    private readonly ExternalToolsNote _externalTools = new();
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
    // UI-таймеры качают Core-контроллеры (heartbeat, draft) — Core без WPF.
    private System.Windows.Threading.DispatcherTimer _draftTimer;
    private System.Windows.Threading.DispatcherTimer _heartbeatTimer;
    // Оконный интерактив инструментов (подтверждение shell) поверх FSM.
    private readonly ChatInteraction _interaction;
    // Жизненный цикл: реестр стартуемых/останавливаемых сервисов.
    private readonly AppLifecycle _lifecycle;
    // Драфт окошка ввода: автосохранение в sessions/<id>/draft.txt (переживает обрыв питания).
    private readonly DraftKeeper _draft;
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

    /// <summary>Пуш на GitHub при самосборке (rebuild_self). По умолчанию выкл.</summary>
    public bool PushOnRebuild {
        get => S.PushOnRebuild;
        set => Set(S.PushOnRebuild, value, (s, v) => s.PushOnRebuild = v);
    }

    /// <summary>
    /// Режим диагностики: детальный трейс в logs/diag-YYYYMMDD.log. Включается без
    /// перезапуска (DiagnosticsLog.SetEnabled) — сразу видно, где процесс стоит.
    /// </summary>
    public bool DiagnosticsMode {
        get => S.DiagnosticsMode;
        set {
            Set(S.DiagnosticsMode, value, (s, v) => s.DiagnosticsMode = v);
            DiagnosticsLog.SetEnabled(value);
            DiagnosticsLog.Log($"diagnostics mode {(value ? "ON" : "OFF")} (UI)");
        }
    }

    /// <summary>Интервал автосохранения драфта окошка ввода (сек). 0 = выключено.</summary>
    public int DraftSaveIntervalSeconds {
        get => S.DraftSaveIntervalSeconds;
        set => Set(S.DraftSaveIntervalSeconds, value, (s, v) => s.DraftSaveIntervalSeconds = v);
    }
    public string PushRepo {
        get => S.PushRepo;
        set => Set(S.PushRepo, value, (s, v) => s.PushRepo = v);
    }

    /// <summary>Компаньон-модель (логит-пробы, векторизация памяти) — отдельная машина.</summary>
    public string CompanionEndpoint {
        get => S.CompanionEndpoint;
        set => Set(S.CompanionEndpoint, value, (s, v) => s.CompanionEndpoint = v);
    }
    /// <summary>Использовать ли companion-модель для проб (тумблер рядом с адресом). Выкл — пробы не летят, память по тексту; адрес сохранён.</summary>
    public bool CompanionEnabled {
        get => S.CompanionEnabled;
        set => Set(S.CompanionEnabled, value, (s, v) => s.CompanionEnabled = v);
    }
    /// <summary>Мастер-переключатель памяти агента (вручную). Выкл — реколл/state-блок/наг/flush и тулы memory_* выключены.</summary>
    public bool MemoryEnabled {
        get => S.MemoryEnabled;
        set => Set(S.MemoryEnabled, value, (s, v) => s.MemoryEnabled = v);
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
    public bool MemoryNagEnabled {
        get => S.MemoryNagEnabled;
        set => Set(S.MemoryNagEnabled, value, (s, v) => s.MemoryNagEnabled = v);
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

    /// <summary>
    /// Кнопка «×» (удаление сессии) видна только для не-main сессий: main удалить нельзя,
    /// поэтому нажимать на кнопку у main незачем.
    /// </summary>
    public bool CanDeleteSelectedSession =>
        SelectedSession is not null && SelectedSession.Id != MainAgent.SessionId;

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
        StartupTrace.Log("MainViewModel ctor: begin");
        // Композиционный корень главного чата: граф сервисов собирается здесь (единственное
        // место, знающее порядок). Цикла pipeline⇄stateBlocks больше нет — кэш серверных
        // фактов живёт в ServerProps, оба читают его независимо.
        _log.Changed += OnLogChanged;
        _background = new BackgroundWork(status => StatusText = status);
        TurnsPanel = new TurnPanel(_background.Turns);

        _serviceLlm = new ServiceCompletionClient(
        () => Endpoint,
        () => BuildOptions(ServiceCompletionClient.MaxTokens));
        // Доска сообщений state-блока: pull-анонсеры (состояние на момент рендера) +
        // BoardAnnouncer — дрейн статичной мусорки (push из кода без интерфейса).
        _stateBlocks = new StateBlockBuilder(
        _log.AssignPendingIds,
        () => _log.NextMessageId,
        () => EffectiveContextSize,
        _serverProps,
        () => _log,
        () => _memorySurfacer.GetSurfacedForStateBlock(),
        [_memorySurfacer, new BoardAnnouncer()],
        () => _pairsStore.Pending);
        _pipeline = new PromptPipeline(
        () => _log,
        ResolveSystemPrompt,
        _toolRegistry,
        _serverProps,
        messages => _stateBlocks.Build(),
        ct => MultimodalContext.BuildAsync(SessionDir(), Endpoint, _serverProps, ct),
        activeShelves: EffectiveShelves);

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
        SaveCurrent),
        onCompacted: DeactivateUnusedShelves);

        // Интерактив инструментов (подтверждение shell) — pull-модель: оконные
        // провайдеры живут в ChatInteraction, Core не знает про окна и FSM.
        _interaction = new ChatInteraction(_chatState);
        _interaction.Register();

        // Жизненный цикл: единая точка старта/остановки сервисов (закрытие — LIFO, без бросков).
        _lifecycle = new AppLifecycle(status => StatusText = status);
        // Драфт окошка ввода: создаём ДО EnsureMainSession/RestoreLastSession — они идут
        // через LoadSession, которая пользуется _draft (Flush/Restore). Регистрация здесь,
        // запуск таймера — в StartAll (в конце конструктора).
        _draft = new DraftKeeper(
        () => InputText,
        text => InputText = text,
        () => _sessions.CurrentId,
        new SessionDraftStore(ChatSessions.Root),
        () => DraftSaveIntervalSeconds);
        // Таймер — за UI (Core-класс без таймера): интервал перечитывается на каждом
        // тике, поэтому смена в настройках действует без рестарта (как раньше).
        _draftTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(_draft.IntervalSeconds) };
        _draftTimer.Tick += (_, _) =>
        {
            _draftTimer.Interval = TimeSpan.FromSeconds(_draft.IntervalSeconds);
            _draft.Tick();
        };
        _lifecycle.Register(new DelegateAppService("draft",
            start: () => _draftTimer.Start(),
            shutdown: () => { _draftTimer.Stop(); _draft.Flush(); }));

        Messages.CollectionChanged += (_, _) => {
            RerollCommand.NotifyCanExecuteChanged();
            ContinueCommand.NotifyCanExecuteChanged();
            RefreshPromptPreview();
        };

        // Вложения к следующему сообщению: SendCommand.canexec меняется (можно отправить
        // и картинку без текста) + чипсы в UI.
        PendingAttachments.CollectionChanged += (_, _) => SendCommand.NotifyCanExecuteChanged();
        StartupTrace.Log("MainViewModel ctor: EnsureMainSession");
        EnsureMainSession();
        StartupTrace.Log("MainViewModel ctor: RestoreLastSession");
        RestoreLastSession();
        // Восстановить драфт ТЕКУЩЕЙ сессии (main или последней открытой) в окошко ввода:
        // переживает обрыв питания/крах — набранный промпт возвращается.
        _draft.Restore();
        StartupTrace.Log("MainViewModel ctor: RefreshPromptPreview (startup)");
        RefreshPromptPreview();

        // Вкладка «Диагностика»: стекло в состояние FSM, бюджет контекста, сборки, память.
        Diagnostics = new DiagnosticsViewModel(
        _chatState,
        () => _serverProps.LastActualPromptTokens(_log),
        () => EffectiveContextSize,
        () => MaxTokens);

        // Вкладка «Суммаризация»: ре-прогоны и редактирование резюме/слоёв/промптов.
        Summarization = new SummarizationViewModel(RunSummarizationCallAsync);

        // MCP: register tools after initial connection completes (non-blocking).
        // Dispatch на UI-поток (ToolRegistry — общий с UI). Если MainWindow ещё не
        // создан (редкий гонок) — ретрай через DispatcherTimer, пока не появится.
        // Хук перерегистрации MCP-тулов (mcp_reload): Core не знает про UI, UI знает
        // про поток реестра (паттерн AgentInteraction).
        McpService.ReRegisterTools = () =>
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher is { } dispatcher)
            {
                dispatcher.Invoke(RegisterMcpTools);
            }
            else
            {
                RegisterMcpTools();
            }
        };
        _ = McpService.Ready.ContinueWith(_ =>
        {
            StartupTrace.Log("MCP: Ready fired (background thread)");
            var app = System.Windows.Application.Current;
            if (app is null)
            {
                StartupTrace.Log("MCP: Application.Current is null, cannot register.");
                System.Diagnostics.Debug.WriteLine("[MCP] Application.Current is null, cannot register.");
                return;
            }
            void TryRegister(int attempt)
            {
                var vm = app.MainWindow?.DataContext as MainViewModel;
                if (vm is null)
                {
                    if (attempt < 20)
                    {
                        StartupTrace.Log($"MCP: MainWindow not ready (attempt {attempt}), retrying.");
                        System.Diagnostics.Debug.WriteLine($"[MCP] MainWindow not ready (attempt {attempt}), retrying.");
                        var timer = new System.Windows.Threading.DispatcherTimer
                            { Interval = TimeSpan.FromMilliseconds(250) };
                        timer.Tick += (_, _) => { timer.Stop(); TryRegister(attempt + 1); };
                        timer.Start();
                    }
                    else
                    {
                        StartupTrace.Log("MCP: MainWindow never appeared, MCP tools NOT registered.");
                        System.Diagnostics.Debug.WriteLine("[MCP] MainWindow never appeared, MCP tools NOT registered.");
                    }
                    return;
                }
                StartupTrace.Log("MCP: RegisterMcpTools begin (UI thread)");
                var sw = Stopwatch.StartNew();
                vm.RegisterMcpTools();
                StartupTrace.Log($"MCP: RegisterMcpTools done ({sw.ElapsedMilliseconds}ms)");
            }
            StartupTrace.Log("MCP: Dispatcher.Invoke dispatched");
            app.Dispatcher.Invoke(() => TryRegister(1));
            StartupTrace.Log("MCP: Dispatcher.Invoke returned");
        }, TaskScheduler.Default);

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
        watchdogGuard: WatchdogLauncher.EnsureAlive);
        // Качание тиков — за UI (паттерн NekoBot: DispatcherTimer UI качает Core-контроллер).
        _heartbeatTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _heartbeatTimer.Tick += (_, _) => _heartbeat.Tick();
        _lifecycle.Register(new DelegateAppService("heartbeat",
            start: () => _heartbeatTimer.Start(),
            shutdown: () => _heartbeatTimer.Stop()));

        // Настройки: закрытие приложения — единственный синхронный flush (дебаунс не гарантирован).
        _lifecycle.Register(new DelegateAppService("настройки", shutdown: FlushSettingsSave));
        // Настройки, изменённые агентом изнутри (инструмент set_setting): живой экземпляр уже
        // обновлён и записан, остаётся перерисовать биндинг. Событие может прийти из
        // agent-потока → маришализуем на Dispatcher (см. OnSettingsChangedExternally).
        SettingsStore<AppSettings>.Changed += OnSettingsChangedExternally;
        StartupTrace.Log("MainViewModel ctor: lifecycle StartAll");
        _lifecycle.StartAll();
        StartupTrace.Log("MainViewModel ctor: done");

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
            : BuildRestartFailureMessage(entry);
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

    /// <summary>
    /// Сообщение о провале рестарта: причина + (если есть) свежий крах новой сборки —
    /// чтобы сразу было видно, в какой строке упало, без копания в logs/last-crash.log.
    /// </summary>
    private static string BuildRestartFailureMessage(BuildJournalEntry entry) {
        var sb = new StringBuilder();
        sb.Append($"[перезапуск] сборка {entry.Id} провалилась ({entry.FailureReason}). Выполнен откат на предыдущую версию.");
        if (!string.IsNullOrWhiteSpace(entry.CrashExcerpt)) {
            sb.Append("\n\n");
            sb.Append(entry.CrashExcerpt);
        }
        return sb.ToString();
    }
    public void ResumePendingChain() {
        var sw = Stopwatch.StartNew();
        StartupTrace.Log($"ResumePendingChain: begin (busy={_chatState.IsBusy}, messages={_log.Count}, last={(_log.Count > 0 ? _log[^1].Role.ToString() : "none")})");
        AnnounceRestarts();
        // Гвард по FSM, а не по IsGenerating: при Compacting/Awaiting* второй ход
        // бросил бы InvalidOperationException внутри Transition.

        if (!_chatState.IsBusy && _log.Count > 0 && _log[^1].Role == ChatRole.Tool) {
            StartupTrace.Log("ResumePendingChain: starting tool-chain continuation");
            _background.Queue("продолжение цепочки tool", () => GenerateAsync());
        }
        StartupTrace.Log($"ResumePendingChain: done ({sw.ElapsedMilliseconds}ms)");
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
        var sw = Stopwatch.StartNew();
        var data = _sessions.EnsureMain();

        if (data is not null) {
            StartupTrace.Log($"EnsureMainSession: loaded {data.Messages.Count} messages ({sw.ElapsedMilliseconds}ms)");
            _log.ReplaceAll(StripBakedSystem(data.Messages));
            _log.SetNextMessageId(data.NextMessageId);
        }
        else {
            StartupTrace.Log($"EnsureMainSession: created empty ({sw.ElapsedMilliseconds}ms)");
            _log.Clear();
        }
        SaveCurrent();
        StartupTrace.Log($"EnsureMainSession: saved ({sw.ElapsedMilliseconds}ms total)");
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
        if (!S.MemoryEnabled || _memoryFlushInFlight || _chatState.IsBusy) 
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
        OnPropertyChanged(nameof(CanDeleteSelectedSession));
        if (value is null || value.Id == _sessions.CurrentId || IsGenerating) 
            return;
        
        LoadSession(value.Id);
        RefreshShelfUi(); // полки per-session — меню следует за выбранной сессией
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
        RefreshShelfUi(); // свежая сессия стартует без полок (по дефолту всё выключено)
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

        // Удаление необратимо (chat.json + artifacts сессии) — подтверждаем.
        var confirm = new Views.ConfirmWindow($"Удалить сессию «{SelectedSession.Title}»?")
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (confirm.ShowDialog() != true)
            return;

        if (_sessions.Delete(SelectedSession.Id)) {
            // Удалили текущую: ChatSessions уже переключился на свежую пустую.
            _log.Clear();
        }

        RefreshSessions();
        RefreshPromptPreview();
        RefreshShelfUi();
        _sessions.PersistCurrentId();
    }

    private bool LoadSession(string id) {
        var sw = Stopwatch.StartNew();
        // ПЕРЕД сменой: выгрести текст текущей (старой) сессии в ЕЁ драфт — иначе
        // набранный за последние секунды черновик потеряется при переключении.
        _draft.Flush();
        var data = _sessions.Load(id);
        if (data is null) {
            StartupTrace.Log($"LoadSession({id}): not found ({sw.ElapsedMilliseconds}ms)");
            return false;
        }
        StartupTrace.Log($"LoadSession({id}): parsed {data.Messages.Count} messages ({sw.ElapsedMilliseconds}ms)");

        _log.ReplaceAll(id == MainAgent.SessionId ? StripBakedSystem(data.Messages) : data.Messages);
        _log.SetNextMessageId(data.NextMessageId);
        _samplerKey = data.SamplerKey;
        _promptKey = data.PromptKey;
        _stateBlockKey = data.StateBlockKey;
        // Смена сессии: surfaced-пул памяти и мусорка анонсов — транзитное состояние
        // прошлой сессии, не тащим его в новую (иначе чужие заметки просочатся в state-блок).
        _memorySurfacer.Clear();
        AnnouncementBoard.Clear();
        // ПОСЛЕ смены: восстановить драфт НОВОЙ сессии в окошко (у каждой свой черновик).
        _draft.Restore();
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
    // ── Полки в UI (кнопка 🗄 в тулбаре чата) ─────────────────────────────────────
    // Тот же механизм, что и у тулов агента: состояние — sessions/<id>/shelves.json,
    // активация немедленная, деактивация staged (снимется при ближайшей естественной
    // смене промпта). Состояние меню синхронизируется из файла: при открытии меню,
    // смене сессии, переключении и на каждый запрос (ResolveSystemPrompt — подхватывает
    // изменения тулами агента).
    private readonly ShelfUiState[] _shelfUi =
    {
        new(ToolGroup.Browser, "WebView2-браузер: навигация, клики, ввод текста, скриншоты, JS, консоль и сетевые логи"),
        new(ToolGroup.CSharp, "Анализ кода Roslyn: символы, ссылки, диагностика, outline, class map"),
        new(ToolGroup.Desktop, "Рабочий стол: мышь, клавиатура, скриншоты, окна"),
        new(ToolGroup.Mcp, "Инструменты управления MCP (mcp_status, mcp_reload) и тулы подключённых MCP-серверов"),
    };

    public ShelfUiState BrowserShelf => _shelfUi[0];
    public ShelfUiState CSharpShelf => _shelfUi[1];
    public ShelfUiState DesktopShelf => _shelfUi[2];
    public ShelfUiState McpShelf => _shelfUi[3];

    private int _shelfCount;
    /// <summary>Сколько полок реально в промпте (on + pending) — счётчик на кнопке «🗄 N».</summary>
    public int ShelfCount {
        get => _shelfCount;
        private set {
            if (_shelfCount == value) return;
            _shelfCount = value;
            OnPropertyChanged(nameof(ShelfCount));
            OnPropertyChanged(nameof(HasActiveShelves));
        }
    }
    public bool HasActiveShelves => ShelfCount > 0;

    /// <summary>
    /// Переключить полку из UI-меню — тот же вход, что и у тулов агента (ShelfState.Activate/
    /// Deactivate): отметка → немедленная активация (отменяет pending), снятие → staged-
    /// деактивация. Направление — по состоянию чекбокса («on» = активна И не помечена):
    /// on → помечаем к снятию; off/pending → активируем (pending-группа всё ещё в active,
    /// смотреть только на active нельзя — иначе повторный клик по pending снова пометит её).
    /// </summary>
    [RelayCommand]
    private void ToggleShelf(string? group) {
        if (!ActivateShelfTool.TryParseGroup(group ?? string.Empty, out var g))
            return;
        var state = new ShelfState(SessionDir());
        var isOn = state.Load().Contains(g) && !state.LoadPending().Contains(g);
        var result = isOn ? state.Deactivate(g) : state.Activate(g);
        // Desktop: полка ушла в pending — скрываем оверлей курсора (как тул агента).
        if (g == ToolGroup.Desktop && state.LoadPending().Contains(g))
            DesktopOverlay.Hide();
        Debug.WriteLine($"[shelf-cache] UI: {g} → {result}");
        RefreshShelfUi();
    }

    /// <summary>
    /// Синхронизировать состояние меню полок с shelves.json текущей сессии. Dispatcher-safe:
    /// вызывается из UI (смена сессии, переключение, открытие меню) и из agent loop
    /// (ResolveSystemPrompt на каждый запрос — подхватывает переключения тулами агента).
    /// </summary>
    public void RefreshShelfUi() {
        void Do() {
            var shelf = new ShelfState(SessionDir());
            var active = shelf.Load();
            var pending = shelf.LoadPending();
            var count = 0;
            foreach (var s in _shelfUi) {
                var inPrompt = active.Contains(s.Group) || pending.Contains(s.Group);
                s.Refresh(active.Contains(s.Group), pending.Contains(s.Group));
                if (inPrompt) count++;
            }
            ShelfCount = count;
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Do);
        else
            Do();
    }

    private string? _cachedSystemPrompt;
    // Base-промпт (без индекса полок) — для детекта ЕСТЕСТВЕННОЙ смены промпта: именно она
    // меняется при компакции/смене сессии/слоях. С её сменой батчим staged-деактивации.
    private string? _cachedBasePrompt;

    private string? ResolveSystemPrompt() {
        var isMain = _sessions.CurrentId == MainAgent.SessionId;
        // Ядро: main — динамическая идентичность (main-agent.md + траектория),
        // не-main — профиль. Слои L1/L2/L3 инжектятся ниже ОТДЕЛЬНО и независимо от ядра:
        // пустой (default) профиль их не глотает — баг 2026-09-04: после компакции слои
        // писались в sessions/<id>/layers.json, но в промпт (и превью) не попадали, т.к.
        // RenderSystemPrompt() для пустого профиля возвращает null.
        string? core = isMain
            ? _identity.GetFor(true)
            : ChatProfiles.Get().ResolvePrompt(_promptKey).RenderSystemPrompt();

        // Секция «внешние инструменты» (external/README.md) — всем интерактивным сессиям.
        var note = _externalTools.Get();
        // Слои памяти L1/L2/L3 (per-session, sessions/<id>/layers.json) — дистиллированная
        // история; у main и не-main один и тот же путь (CurrentId = "main" / id сессии).
        var layers = new MemoryLayerStore(
            Path.Combine(SelfBuildPaths.WorkspaceRoot, "sessions", _sessions.CurrentId)).Load();
        var layersBlock = layers.IsEmpty ? string.Empty : layers.ToPromptBlock();

        // Base-промпт (без индекса полок) — для детекта ЕСТЕСТВЕННОЙ смены промпта: именно
        // он меняется при компакции/смене сессии/слоях. С его сменой батчим staged-деактивации.
        var basePrompt = Combine(Combine(core, note), layersBlock.Length > 0 ? layersBlock : null);

        // Staged-деактивации: снимаем помеченные группы ТОЛЬКО когда base-промпт и так меняется
        // (компакция/смена сессии/слои) — деактивация батчится с неизбежным rebuild'ом, а не
        // создаёт собственный. Пока base не менялась — pending-группы остаются в промпте.
        var shelf = new ShelfState(SessionDir());
        var pending = shelf.LoadPending();
        if (pending.Count > 0 && !string.Equals(basePrompt, _cachedBasePrompt, StringComparison.Ordinal))
        {
            var removed = shelf.FlushPending().ToList();
            if (removed.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[shelf-cache] staged-деактивация при смене промпта: {string.Join(", ", removed)}");
            }
        }
        _cachedBasePrompt = basePrompt;

        // Индекс полок: pending-группы рисуются как активные (их тулзы реально ещё в промпте).
        // Вставляется ПЕРЕД слоями: системный промпт течёт в чат как
        // «кто я → какие инструменты → старая история → чуть новейшая → последняя → чат».
        var mcpRows = BuildMcpServerRows();
        var index = ToolGroupIndex.Render(EffectiveShelves(), _toolRegistry, mcpRows);
        var beforeLayers = Combine(Combine(core, note), index.Length > 0 ? index : null);
        var final = Combine(beforeLayers, layersBlock.Length > 0 ? layersBlock : null);
        // Трекер кешированного промпта: изменился → KV-кеш пересоберётся (диагностика).
        if (!string.Equals(_cachedSystemPrompt, final, StringComparison.Ordinal))
        {
            _cachedSystemPrompt = final;
            System.Diagnostics.Debug.WriteLine(
                $"[shelf-cache] системный промпт изменился (KV-кеш rebuild), длина={final?.Length ?? 0}");
        }
        // Меню полок подхватывает изменения (в т.ч. тулами агента в этом же запросе).
        RefreshShelfUi();
        return final;
    }

    /// <summary>Склейка двух фрагментов промпта пустой строкой; null/пусто — второй фрагмент как есть.</summary>
    private static string? Combine(string? a, string? b) =>
        a is null ? b : b is null ? a : a + "\n\n" + b;

    /// <summary>
    /// Тулзы для запроса: базовый набор (Core + активные полки) ∩ whitelist профиля (если задан).
    /// То же множество даёт превью (PromptPipeline.AdvertisedTools) — превью и запрос совпадают.
    /// </summary>
    private IReadOnlyList<ToolDefinition> ShelfFilteredTools(IReadOnlyList<string> allowed)
    {
        var tools = new List<ToolDefinition>(_toolRegistry.DefinitionsByGroup(ToolGroup.Core));
        foreach (var group in EffectiveShelves())
        {
            tools.AddRange(_toolRegistry.DefinitionsByGroup(group));
        }
        // Память выключена — memory_*-тулы не рекламируем (модель не может их вызвать).
        tools = tools.Where(d => MemoryToolGate.ShouldAdvertise(d.Name)).ToList();
        if (allowed.Count == 0)
        {
            return tools;
        }
        var allow = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tools.Where(d => allow.Contains(d.Name)).ToList();
    }

    /// <summary>
    /// Зарегистрировать MCP-тулы в реестре (вызывается после MCP init и при mcp_reload).
    /// </summary>
    internal void RegisterMcpTools()
    {
        var manager = McpService.Instance;
        if (manager is null) return;
        var (registered, warnings) = McpToolRegistrar.RegisterAll(_toolRegistry, manager);
        System.Diagnostics.Debug.WriteLine($"[MCP] Registered {registered} tools.");
        foreach (var w in warnings)
            System.Diagnostics.Debug.WriteLine($"[MCP] WARNING: {w}");
    }

    /// <summary>Строки для таблицы MCP Servers в системном промпте.</summary>
    private IReadOnlyList<ToolGroupIndex.McpServerRow> BuildMcpServerRows()
    {
        var settings = AppSettings.Get();
        if (settings.McpServers.Count == 0)
            return Array.Empty<ToolGroupIndex.McpServerRow>();

        var manager = McpService.Instance;
        var clients = manager?.Clients ?? new Dictionary<string, McpClient>();
        var mcpShelfActive = EffectiveShelves().Contains(ToolGroup.Mcp);

        return settings.McpServers.Select(s =>
        {
            clients.TryGetValue(s.Name, out var client);
            var connected = client?.IsConnected ?? false;

            // Status: active (shelf on + connected), inactive (shelf off), disconnected (no conn)
            string status;
            if (!connected) status = "disconnected";
            else if (mcpShelfActive) status = "active";
            else status = "inactive";

            // Address: http → URL; stdio → command + args (or env port if present)
            string address;
            if (s.Transport == "http")
            {
                address = s.Url;
            }
            else
            {
                // stdio: show command + args, or env port for bridge servers
                var cmd = string.IsNullOrEmpty(s.Command) ? "" :
                    System.IO.Path.GetFileNameWithoutExtension(s.Command) + " " +
                    string.Join(" ", s.Args);
                // Check for port in env (e.g. BLENDER_MCP_PORT). FirstOrDefault на Dictionary
                // возвращает struct KeyValuePair (никогда не null): без совпадения Value null —
                // NRE на .Value (баг 2026-09-14: превью падало на stdio-сервере с пустым Env).
                string? port = null;
                if (s.Env is not null)
                {
                    foreach (var kv in s.Env)
                    {
                        if (kv.Key is not null && kv.Key.Contains("PORT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(kv.Value))
                        {
                            port = kv.Value;
                            break;
                        }
                    }
                }
                if (port is not null)
                {
                    cmd += $" → localhost:{port}";
                }
                address = cmd;
            }

            return new ToolGroupIndex.McpServerRow(
                s.Name,
                status,
                s.Transport,
                address,
                client?.Tools.Count ?? 0,
                string.IsNullOrEmpty(s.Description) ? "—" : s.Description);
        }).ToList();
    }

    /// <summary>
    /// Полки, тулзы которых реально в промпте: активные + pending-деактивации. Pending-группы
    /// ещё не сняты (ждут естественной смены промпта), их тулзы по-прежнему доступны — индекс
    /// и реклама тулов должны совпадать, иначе модель увидит «active» без инструментов.
    /// </summary>
    private IReadOnlyList<ToolGroup> EffectiveShelves()
    {
        var shelf = new ShelfState(SessionDir());
        var active = shelf.Load();
        foreach (var g in shelf.LoadPending())
        {
            active.Add(g);
        }
        return active.OrderBy(g => g).ToList();
    }

    /// <summary>
    /// Авто-выключение неиспользуемых полок после компакции: в оставшемся контексте ни одного
    /// ToolCall из инструментов группы → снимаем. Бесплатно: компакция и так пересобирает
    /// системный промпт, деактивация батчится с неизбежным rebuild'ом (не создаёт собственный).
    /// </summary>
    private void DeactivateUnusedShelves()
    {
        var shelf = new ShelfState(SessionDir());
        var active = shelf.Load();
        if (active.Count == 0)
        {
            return;
        }
        var unused = ShelfState.FindUnused(active, _log, _toolRegistry).ToList();
        if (unused.Count == 0)
        {
            return;
        }
        foreach (var g in unused)
        {
            active.Remove(g);
            shelf.UnmarkPending(g); // если была помечена — решение уже исполнено, пометка не нужна
        }
        shelf.Save(active);
        System.Diagnostics.Debug.WriteLine(
            $"[shelf-cache] авто-выключение после компакции: {string.Join(", ", unused)}");
        RefreshShelfUi(); // меню должно показать снятые полки
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
        
        if (LoadSession(lastId))
            RefreshSessions(); // CurrentId уже переехал — синхронизируем SelectedSession, иначе селектор покажет main
        else
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
        var sw = Stopwatch.StartNew();
        var count = _log.Count;
        var sessionDir = SessionDir();
        Messages.Clear();
        var added = 0;
        foreach (var message in _log) {
            var view = MessageViewModel.FromMessage(RoleName(message), message);
            view.LoadArtifacts(sessionDir);
            Messages.Add(view);
            added++;
            if (added % 50 == 0) {
                StartupTrace.Log($"RebuildMessageViews: {added}/{count} added ({sw.ElapsedMilliseconds}ms)");
            }
        }
        StartupTrace.Log($"RebuildMessageViews: done {added} messages ({sw.ElapsedMilliseconds}ms)");
    }

    private static string RoleName(ChatMessage message) => message.Role.ToString().ToLowerInvariant();
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync() {
        var text = InputText.Trim();
        InputText = string.Empty;
        // Текст отправлен (стал сообщением) — драфт удаляем, чтобы не восстанавливать
        // отправленное при следующем старте.
        _draft.ClearOnSend();
        var userMessage = ChatMessage.User(text);
        _log.Add(userMessage); // ID присваивается здесь же — до копирования вложений
        var attachments = PendingAttachments.ToList();
        PendingAttachments.Clear();
        var metaStore = new MessageMetaStore(SessionDir());
        var failedAttachments = new List<string>();
        var announcedPaths = new List<string>();

        foreach (var attachment in attachments) {
            try {
                if (attachment.IsImage) {
                    // Мультимодальное: копируем в artifacts/msg_<id>/ (рендер добавит маркер + base64).
                    metaStore.AddArtifact(userMessage.Id, attachment.FullPath);
                } else {
                    // Анонсируемое (не мультимодальное): копируем в attachments/ и анонсируем
                    // тегом <attachment> в сообщении — я читаю файл через read_file.
                    announcedPaths.Add(metaStore.AddFileArtifact(userMessage.Id, attachment.FullPath));
                }
            }
            catch {
                // файл не прочитался — пропускаем вложение, но сообщаем: иначе оно молча
                // не доедет и ход пройдёт «вслепую»
                failedAttachments.Add(attachment.Name);
            }
        }

        // Анонсируем немультимодальные вложения в конце сообщения (путь — относительно workspace).
        if (announcedPaths.Count > 0) {
            var tags = string.Join("\n", announcedPaths.Select(p => $"<attachment path=\"{ToWorkspaceRelative(p)}\">"));
            userMessage.Content = (userMessage.Content + "\n" + tags).Trim();
        }

        if (failedAttachments.Count > 0) {
            StatusText = $"вложение не прикреплено: {string.Join(", ", failedAttachments)}";
        }

        var userView = MessageViewModel.FromMessage("user", userMessage);
        userView.LoadArtifacts(SessionDir());
        Messages.Add(userView);
        await GenerateAsync();
        SaveCurrent();
    }

    /// <summary>Путь относительно корня workspace (для read_file и тега &lt;attachment&gt;).</summary>
    private static string ToWorkspaceRelative(string path) {
        var wsRoot = SelfBuildPaths.WorkspaceRoot;
        return path.StartsWith(wsRoot, StringComparison.OrdinalIgnoreCase)
            ? path[wsRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;
    }

    private bool CanSend() => !IsBusy && (InputText.Trim().Length > 0 || PendingAttachments.Count > 0) && Endpoint.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(IsGenerating))]
    private void Cancel() => _cancellation?.Cancel();

    /// <summary>
    /// Очистка текущего разговора — программный доступ (Harness). UI-кнопка «Очистить»
    /// убрана (2026-09-02): сценарий покрывает «откат» первого сообщения.
    /// </summary>
    public void Clear() {
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
        // Все файлы — во вложения. Картинки уходят мультимодально (маркер + base64 в рендере),
        // остальные (txt, pdf, ...) — как анонсируемые аттачменты (attachments/ + тег
        // <attachment> в сообщении), я читаю их через read_file. Текст в ввод больше не
        // вставляется: не раздувает сообщение и не обрезает крупные файлы.
        foreach (var file in dialog.FileNames) {
            PendingAttachments.Add(new PendingAttachment(Path.GetFileName(file), file));
        }
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

    /// <summary>Скрыть панель live-превью сжатия («×» на панели).</summary>
    [RelayCommand]
    private void HideCompactionPanel() => _compaction.Hide();

    /// <summary>Открыть панель сжатия из тулбара (кнопка активна, когда в панели есть что показывать).</summary>
    [RelayCommand]
    private void ShowCompactionPanel() => _compaction.Open();
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
    private int _previewRenderCount;
    private void RefreshPromptPreview() {
        var sw = Stopwatch.StartNew();
        try {
            var preview = _pipeline.RenderForPreview();
            PromptPreview = preview.Length == 0 ? "(пусто)" : preview;
            _previewRenderCount++;
            if (_previewRenderCount <= 10 || _previewRenderCount % 50 == 0 || sw.ElapsedMilliseconds > 500) {
                StartupTrace.Log($"RefreshPromptPreview #{_previewRenderCount}: {preview.Length} chars ({sw.ElapsedMilliseconds}ms)");
            }
        }
        catch (Exception exception) {
            PromptPreview = $"[не удалось отрендерить: {exception.Message}]";
            StartupTrace.Log($"RefreshPromptPreview #{++_previewRenderCount}: FAILED ({exception.Message})");
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
                ToolDefinitions = toolsAllowed ? ShelfFilteredTools(prompt.AllowedTools) : Array.Empty<ToolDefinition>(),
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