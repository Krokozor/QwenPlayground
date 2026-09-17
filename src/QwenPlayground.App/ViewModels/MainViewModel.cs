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
using QwenPlayground.Core.Mcp;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Main;
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
    // Композиционный корень (Core/Main): граф сервисов собирается там, UI — протокол-адаптер
    // (пузыри, команды, тонкие виды). Хуки UI — через UiHooks в конструкторе.
    private readonly Main _main;
    // Структурные изменения разговора (компакция/загрузка/откат) сами перестраивают вид.
    private void OnLogChanged() => RebuildMessageViews();
    /// <summary>Каталог текущей сессии: у каждой сессии своя папка sessions/&lt;id&gt;/ (как у main-агента).</summary>
    private string SessionDir() => _main.Sessions.DirectoryFor(_main.Sessions.CurrentId);

    /// <summary>UI-диспетчер ходов (heartbeat/wake/flush видны списком, не одной строкой).</summary>
    public TurnPanel TurnsPanel { get; private set; } = null!;
    // UI-таймеры качают Core-контроллеры (heartbeat, draft) — Core без WPF.
    private System.Windows.Threading.DispatcherTimer _draftTimer;
    private System.Windows.Threading.DispatcherTimer _heartbeatTimer;
    // Оконный интерактив инструментов (подтверждение shell) поверх FSM — провайдеры окон,
    // поэтому живёт в App (Core не знает про окна).
    private readonly ChatInteraction _interaction;

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
    public bool IsBusy => _main.ChatState.IsBusy;

    /// <summary>Живое превью компакции (панель, стадии, стриминг токенов).</summary>
    public CompactionPreview Compaction => _main.Compaction;

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
    // Ключи кусков живут в SessionController (перистируются в SessionData), VM читает их
    // через контроллер.

    /// <summary>main-сессия управляется идентичностью — настройка чата для неё закрыта.</summary>
    public bool IsMainSession => _main.Sessions.CurrentId == MainAgent.SessionId;

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
        // Композиционный корень (Core/Main): граф сервисов собирается там (единственное
        // место, знающее порядок). Хуки UI — через UiHooks: фасад не знает про WPF.
        _main = new Main(new UiHooks(
            status => StatusText = status,
            generating => IsGenerating = generating,
            () => InputText,
            text => InputText = text,
            SaveCurrent,
            RunHeartbeatTurnAsync,
            FlushMemoryVectorsAsync,
            RefreshShelfUi, // меню должно показать снятые полки
            () => System.Windows.Application.Current?.Shutdown()),
            typeof(AgentTool).Assembly, // Core: базовые инструменты
            typeof(MainViewModel).Assembly); // App: UI-инструменты (screenshot, switch_tab)
        _main.Log.Changed += OnLogChanged;
        _main.Sessions.SessionChanged += OnSessionChanged;
        // Снятие полки (тулом агента или из меню) — доменное событие; реакция UI — здесь.
        ShelfState.Deactivated += OnShelfDeactivated;
        TurnsPanel = new TurnPanel(_main.Background.Turns);

        // Интерактив инструментов (подтверждение shell) — pull-модель: оконные
        // провайдеры живут в ChatInteraction (App), Core не знает про окна и FSM.
        _interaction = new ChatInteraction(_main.ChatState);
        _interaction.Register();

        // Таймер — за UI (Core-класс без таймера): интервал перечитывается на каждом
        // тике, поэтому смена в настройках действует без рестарта (как раньше).
        _draftTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(_main.Draft.IntervalSeconds) };
        _draftTimer.Tick += (_, _) =>
        {
            _draftTimer.Interval = TimeSpan.FromSeconds(_main.Draft.IntervalSeconds);
            _main.Draft.Tick();
        };
        _main.Lifecycle.Register(new DelegateAppService("draft",
            start: () => _draftTimer.Start(),
            shutdown: () => { _draftTimer.Stop(); _main.Draft.Flush(); }));

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
        _main.Draft.Restore();
        StartupTrace.Log("MainViewModel ctor: RefreshPromptPreview (startup)");
        RefreshPromptPreview();

        // Вкладка «Диагностика»: стекло в состояние FSM, бюджет контекста, сборки, память.
        Diagnostics = new DiagnosticsViewModel(
        _main.ChatState,
        () => _main.ServerProps.LastActualPromptTokens(_main.Log),
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

        // Качание тиков heartbeat — за UI (паттерн NekoBot: DispatcherTimer UI качает
        // Core-контроллер; сам контроллер собран в Main).
        _heartbeatTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _heartbeatTimer.Tick += (_, _) => _main.Heartbeat.Tick();
        _main.Lifecycle.Register(new DelegateAppService("heartbeat",
            start: () => _heartbeatTimer.Start(),
            shutdown: () => _heartbeatTimer.Stop()));

        // Настройки: закрытие приложения — единственный синхронный flush (дебаунс не гарантирован).
        _main.Lifecycle.Register(new DelegateAppService("настройки", shutdown: FlushSettingsSave));
        // Настройки, изменённые агентом изнутри (инструмент set_setting): живой экземпляр уже
        // обновлён и записан, остаётся перерисовать биндинг. Событие может прийти из
        // agent-потока → маришализуем на Dispatcher (см. OnSettingsChangedExternally).
        SettingsStore<AppSettings>.Changed += OnSettingsChangedExternally;
        StartupTrace.Log("MainViewModel ctor: lifecycle StartAll");
        _main.Lifecycle.StartAll();
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
        sb.AppendLine($"session: {_main.Sessions.CurrentId}");
        sb.AppendLine($"chat FSM: {_main.ChatState.Current}; generating: {IsGenerating}");
        // QwenPlayground.Core.Runtime.TurnState: вложенный класс TurnState хода затеняет имя.
        var active = _main.Background.Turns.Turns
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
        return _main.Lifecycle.ShutdownAll();
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
            if (_main.Log.Count > 0 && _main.Log[^1].Role == ChatRole.Tool) {
                _main.Log[^1].Content += "\n" + outcome;
            }

            else {
                _main.Log.Add(ChatMessage.Tool(outcome));
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
        StartupTrace.Log($"ResumePendingChain: begin (busy={_main.ChatState.IsBusy}, messages={_main.Log.Count}, last={(_main.Log.Count > 0 ? _main.Log[^1].Role.ToString() : "none")})");
        AnnounceRestarts();
        // Гвард по FSM, а не по IsGenerating: при Compacting/Awaiting* второй ход
        // бросил бы InvalidOperationException внутри Transition.

        if (!_main.ChatState.IsBusy && _main.Log.Count > 0 && _main.Log[^1].Role == ChatRole.Tool) {
            StartupTrace.Log("ResumePendingChain: starting tool-chain continuation");
            _main.Background.Queue("продолжение цепочки tool", () => GenerateAsync());
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
        var result = await _main.ServiceLlm.CompleteStructuredAsync(userContent, system, onToken, cancellationToken);
        return result ?? string.Empty;
    }

    /// <summary>
    /// Сессия main-агента — сессия по умолчанию: грузим её из sessions/main/chat.json,
    /// иначе начинаем с чистого разговора. Логика — в SessionController; здесь только
    /// обновление списка сессий (вид).
    /// </summary>
    private void EnsureMainSession() {
        _main.Sessions.EnsureMain();
        RefreshSessions();
    }

    /// <summary>
    /// Flush-векторизация памяти: факты без слоёв или со старой LayersVersion классифицируются
    /// на компаньон-модели на фоне (троттлинг + бюджет за проход), чтобы не забивать поток чата.
    /// Ошибки глотаются — не критичный путь; следующее сердцебиение повторит попытку.
    /// </summary>
    private async Task FlushMemoryVectorsAsync() {
        if (!S.MemoryEnabled || _memoryFlushInFlight || _main.ChatState.IsBusy) 
            return;
        
        if (DateTime.UtcNow - _lastMemoryFlushAt < MemoryFlushInterval) 
            return;
        
        _lastMemoryFlushAt = DateTime.UtcNow;
        _memoryFlushInFlight = true;

        try {
            var endpoint = CompanionEndpoint;
            var token = _main.Turns.ActiveToken;
            var store = new MemoryStore();
            var processed = await MemoryClassifier.FlushAsync(store, endpoint, AppSettings.Get().MemoryFlushBudget, token);

            if (processed > 0) {
                StatusText = $"🧠 память: векторизовано {processed} фактов";
            }

            else {
                // Все факты с векторами — надмозг переключается на поиск дубликатов.
                var scan = await MemorySimilarity.ScanPassAsync(
                store, _main.PairsStore, endpoint, AppSettings.Get().MemoryScanProbeBudget,
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
    private void WakeNow() => _main.Heartbeat.WakeNow();

    /// <summary>Ход main-агента по инициативе приложения: всегда агентный режим (иначе бессмысленно).</summary>
    private async Task RunHeartbeatTurnAsync(string prompt) {
        var userMessage = ChatMessage.User(prompt);
        _main.Log.Add(userMessage);
        Messages.Add(MessageViewModel.FromMessage("user", userMessage));
        await GenerateAsync();
        SaveCurrent();
    }

    partial void OnSelectedSessionChanged(SessionInfo? value) {
        OnPropertyChanged(nameof(CanDeleteSelectedSession));
        if (value is null || value.Id == _main.Sessions.CurrentId || IsGenerating) 
            return;
        
        if (_main.Sessions.Load(value.Id))
            StatusText = string.Empty;
        // Реакции вида (список/превью/полки) — в OnSessionChanged (событие контроллера).
    }

    [RelayCommand]
    private void NewSession() {
        if (IsGenerating) 
            return;        

        if (_main.Log.Count > 0) 
            SaveCurrent();        

        _main.Log.Clear();
        _main.Sessions.StartNew();
        // Реакции вида (список/выбор/превью/полки) — в OnSessionChanged (событие контроллера).
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
        _main.Background.Queue("сохранение настроек", async () => {
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

        _main.Sessions.Delete(SelectedSession.Id);
        // Реакции вида (список/превью/полки) — в OnSessionChanged (событие контроллера).
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
        Debug.WriteLine($"[shelf-cache] UI: {g} → {result}");
        RefreshShelfUi();
    }

    /// <summary>
    /// Полка снята (staged-деактивация) — реакция UI на доменное событие ShelfState.Deactivated:
    /// desktop-полка текущего сессии ушла → скрываем оверлей курсора (пользователь закончил
    /// управление десктопом). Оба вызывающих места (тул агента и меню) идут через событие.
    /// Потоки: и тул (инвариант — agent-код на UI-потоке), и меню — UI-поток.
    /// </summary>
    private void OnShelfDeactivated(ToolGroup group, string sessionDir)
    {
        if (group == ToolGroup.Desktop && sessionDir == SessionDir())
        {
            DesktopOverlay.Hide();
        }
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

    /// <summary>
    /// Единый с превью и ходом источник системного промпта: сборка — в SystemPromptAssembler
    /// (Core); здесь только обновление меню полок (UI-состояние после каждого запроса).
    /// </summary>
    private string? ResolveSystemPrompt() {
        var prompt = _main.PromptAssembler.ResolveSystemPrompt();
        RefreshShelfUi(); // меню подхватывает изменения (в т.ч. тулами агента в этом же запросе)
        return prompt;
    }

    /// <summary>
    /// Зарегистрировать MCP-тулы в реестре (вызывается после MCP init и при mcp_reload).
    /// </summary>
    internal void RegisterMcpTools()
    {
        var manager = McpService.Instance;
        if (manager is null) return;
        var (registered, warnings) = McpToolRegistrar.RegisterAll(_main.Tools, manager);
        System.Diagnostics.Debug.WriteLine($"[MCP] Registered {registered} tools.");
        foreach (var w in warnings)
            System.Diagnostics.Debug.WriteLine($"[MCP] WARNING: {w}");
    }


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
            _main.Sessions.SamplerKey, _main.Sessions.PromptKey, _main.Sessions.StateBlockKey)
        { Owner = System.Windows.Application.Current.MainWindow };
        // Пресеты редактируются в ЕДИНСТВЕННОМ месте — вкладка «Настройки»; диалог закрываем.
        dialog.GoToSettings = () => SelectedTabIndex = SettingsTabIndex;
        if (dialog.ShowDialog() == true) {
            _main.Sessions.ApplyProfileKeys(
                dialog.SelectedSamplerKey, dialog.SelectedPromptKey, dialog.SelectedStateBlockKey);
            RefreshPromptPreview();
            StatusText = "Настройка чата применена: действует со следующего хода.";
        }
    }

    private static List<string> OrderedKeys(IEnumerable<string> keys) =>
        keys.OrderBy(k => k == ChatProfileSet.DefaultKey ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Восстановить последнюю открытую сессию (из settings.json). Логика — в
    /// SessionController; реакции вида — в OnSessionChanged.
    /// </summary>
    private void RestoreLastSession() {
        _main.Sessions.RestoreLast();
    }

    public void SaveCurrent() {
        _main.Sessions.SaveCurrent();
        RefreshSessions(); // заголовок/время в списке могли измениться
    }

    private void RefreshSessions() {
        _main.Sessions.RefreshList();
        Sessions.Clear();
        foreach (var info in _main.Sessions.List) {
            Sessions.Add(info);
        }

        SelectedSession = Sessions.FirstOrDefault(s => s.Id == _main.Sessions.CurrentId);
    }

    /// <summary>
    /// Сессия сменилась (событие SessionController): обновить вид — флаг main, список +
    /// выбор (селектор следует за CurrentId), превью, меню полок (полки per-session).
    /// </summary>
    private void OnSessionChanged() {
        OnPropertyChanged(nameof(IsMainSession));
        RefreshSessions();
        RefreshPromptPreview();
        RefreshShelfUi();
    }

    private void RebuildMessageViews() {
        var sw = Stopwatch.StartNew();
        var count = _main.Log.Count;
        var sessionDir = SessionDir();
        Messages.Clear();
        var added = 0;
        foreach (var message in _main.Log) {
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
        _main.Draft.ClearOnSend();
        var userMessage = ChatMessage.User(text);
        _main.Log.Add(userMessage); // ID присваивается здесь же — до копирования вложений
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
    private void Cancel() => _main.Turns.Cancel();

    /// <summary>
    /// Очистка текущего разговора — программный доступ (Harness). UI-кнопка «Очистить»
    /// убрана (2026-09-02): сценарий покрывает «откат» первого сообщения.
    /// </summary>
    public void Clear() {
        _main.Log.Clear();
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

        _main.Log.RemoveFrom(index);
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
    private async Task CompactAsync() => await _main.Maintenance.CompactFromUiAsync();

    /// <summary>Скрыть панель live-превью сжатия («×» на панели).</summary>
    [RelayCommand]
    private void HideCompactionPanel() => _main.Compaction.Hide();

    /// <summary>Открыть панель сжатия из тулбара (кнопка активна, когда в панели есть что показывать).</summary>
    [RelayCommand]
    private void ShowCompactionPanel() => _main.Compaction.Open();
    private bool CanInteract() => !IsBusy;

    /// <summary>
    /// Эффективный размер окна: реальный n_ctx сервера (если известен), иначе настроенный
    /// ContextSize. Для проверки «влезет ли» сравниваем именно с ним — это то, что реально
    /// спрашивает сервер.
    /// </summary>
    private int EffectiveContextSize => Math.Min(ContextSize, _main.ServerProps.NContext ?? ContextSize);
    private int _previewRenderCount;
    private void RefreshPromptPreview() {
        var sw = Stopwatch.StartNew();
        try {
            var preview = _main.Pipeline.RenderForPreview();
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
    GenerateCoreAsync(continueLastAssistant);
    /// <summary>
    /// Ход: доменная оркестрация (бюджет, FSM, AgentLoop, профили, отмена, рестарт) — в
    /// TurnPipeline; здесь — только состояние вида (пузыри) и решение, куда показать ошибку.
    /// </summary>
    private async Task GenerateCoreAsync(bool continueLastAssistant) {
        var agentic = S.ProjectRoot.Trim().Length > 0;
        var continued = continueLastAssistant && _main.Log.Count > 0 &&
        _main.Log[^1].Role == ChatRole.Assistant
        ? _main.Log[^1]
        : null;
        // Состояние одного хода: локальные мутации обработчиков событий собраны вместе.
        var turn = new TurnState { Continued = continued, Agentic = agentic };
        if (continued is not null) {
            turn.CurrentAssistant = Messages[^1];
            turn.Raw.Append(continued.ToRawOutput());
        }
        var outcome = await _main.Turns.RunTurnAsync(continueLastAssistant, e => DispatchEvent(turn, e));
        if (outcome.BudgetFailed) {
            // Бюджет не прошёл — статус и сохранение истории уже сделаны пайплайном.
            return;
        }
        if (outcome.Canceled) {
            // Хвост стрима мог не успеть опубликоваться (троттлинг) — финализируем вид до разбора.
            turn.CurrentAssistant?.FlushStreaming();
            CommitCanceledPartial(turn.Continued, turn.CurrentAssistant, turn.Raw.ToString());
        }
        else if (outcome.Error is { } exception) {
            // В single-режиме исторически показываем ошибку прямо в пузыре ответа.
            if (!outcome.Agentic && turn.CurrentAssistant is not null) {
                turn.CurrentAssistant.Content = $"[ошибка] {exception.Message}";
            }
            else {
                StatusText = $"ошибка: {exception.Message}";
            }
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
            _main.MemorySurfacer.ResetLiveWindow();
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
        _main.MemorySurfacer.MaybeFireLiveRecall(turn.Agentic, text, turn.Raw, turn.Continued is not null,
        _main.Log, _main.Sessions.CurrentId == MainAgent.SessionId,
        CompanionEndpoint, _main.Turns.ActiveToken);
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
            var conversation = _main.Log;
            var companion = CompanionEndpoint;
            var token = _main.Turns.ActiveToken;
            _main.Background.Queue("реколл памяти", () =>
            _main.MemorySurfacer.RecallAfterTurnAsync(
            conversation, _main.Sessions.CurrentId == MainAgent.SessionId, companion, token));
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
        else if (_main.Log.Count > 0 && _main.Log[^1].Role == ChatRole.Assistant) {
            currentAssistant.Source = _main.Log[^1];
        }
        else {
            _main.Log.Add(partial);
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