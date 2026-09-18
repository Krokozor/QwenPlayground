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

    /// <summary>
    /// Вкладка «Настройки»: свой DataContext (инкапсуляция — SettingsView/MemorySettingsView
    /// биндятся на него, а не на MainViewModel). Зеркала AppSettings + отложенный save.
    /// </summary>
    public SettingsViewModel Settings { get; } = new();

    /// <summary>Источник правды настроек — синглтон AppSettings.Get() (тонкие виды UI — в SettingsViewModel).</summary>
    private AppSettings S => AppSettings.Get();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isGenerating;

    /// <summary>
    /// Команды сообщений живут в MessageCommands — их CanExecute (Reroll/Continue)
    /// зависит от IsGenerating: уведомляем модуль, когда флаг переключается.
    /// </summary>
    partial void OnIsGeneratingChanged(bool value) {
        MessageCommands?.NotifyCanExecuteChanged();
    }
    /// <summary>Чат занят (нельзя принимать новые ходы/ручную компакцию). Вычисляется из FSM.</summary>
    public bool IsBusy => _main.ChatState.IsBusy;

    /// <summary>Живое превью компакции (панель, стадии, стриминг токенов).</summary>
    public CompactionPreview Compaction => _main.Compaction;

    public ReasoningEffort ReasoningEffort {
        get => S.ReasoningEffort;
        set {
            // Живёт здесь (не в SettingsViewModel): биндинг тулбара чата + реакция превью.
            var old = S.ReasoningEffort;
            S.ReasoningEffort = value;
            if (old != value) {
                Settings.ScheduleSave();
            }
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

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _promptPreview = string.Empty;

    public ObservableCollection<MessageViewModel> Messages { get; } = new();

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

    /// <summary>
    /// Панель сессий в тулбаре чата (селектор + «+»/«×»): список, выбор, команды.
    /// Логика — в SessionController (Core); реакции на смену — в OnSessionChanged.
    /// </summary>
    public SessionListViewModel SessionList { get; }

    /// <summary>
    /// Команды операций над сообщениями: откат, просмотр промпта, редактирование,
    /// копирование, реколл/продолжение, вложения. Коллекции и состояние чата — здесь,
    /// в VM; в модуль приходят ссылки и делегаты.
    /// </summary>
    public MessageCommandsViewModel MessageCommands { get; }

    /// <summary>Индекс вкладки «Настройки» в главном окне (для перехода из шестерёнки чата).</summary>
    public const int SettingsTabIndex = 2;

    [ObservableProperty]
    private int _selectedTabIndex;

    public MainViewModel() {
        StartupTrace.Log("MainViewModel ctor: begin");
        Shelves = new(() => SessionDir());
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
            Shelves.Refresh, // меню должно показать снятые полки
            () => System.Windows.Application.Current?.Shutdown()),
            typeof(AgentTool).Assembly, // Core: базовые инструменты
            typeof(MainViewModel).Assembly); // App: UI-инструменты (screenshot, switch_tab)
        // LEGO-модули UI: панель сессий, меню полок, команды сообщений (свои DataContext,
        // события/делегаты — швы).
        SessionList = new(_main.Sessions, _main.Log, () => IsGenerating, status => StatusText = status);
        MessageCommands = new(Messages, PendingAttachments, _main.Log,
            CanInteract, () => IsGenerating, continueLast => GenerateAsync(continueLast),
            SaveCurrent, RefreshPromptPreview, status => StatusText = status);
        _main.Log.Changed += OnLogChanged;
        _main.Sessions.SessionChanged += OnSessionChanged;
        // Снятие полки (тулом агента или из меню) — доменное событие; реакция UI — в меню.
        ShelfState.Deactivated += Shelves.OnDeactivated;
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
            MessageCommands.RerollCommand.NotifyCanExecuteChanged();
            MessageCommands.ContinueCommand.NotifyCanExecuteChanged();
            RefreshPromptPreview();
        };

        // Вложения к следующему сообщению: SendCommand.canexec меняется (можно отправить
        // и картинку без текста) + чипсы в UI.
        PendingAttachments.CollectionChanged += (_, _) => SendCommand.NotifyCanExecuteChanged();
        StartupTrace.Log("MainViewModel ctor: EnsureMain");
        SessionList.EnsureMain();
        StartupTrace.Log("MainViewModel ctor: RestoreLast");
        SessionList.RestoreLast();
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
        () => S.MaxTokens);

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
        _main.Lifecycle.Register(new DelegateAppService("настройки", shutdown: () => Settings.FlushSettingsSave()));
        // Настройки, изменённые агентом изнутри (инструмент set_setting): живой экземпляр уже
        // обновлён и записан, остаётся перерисовать биндинг. Событие может прийти из
        // agent-потока → маришализуем на Dispatcher (см. OnSettingsChangedExternally).
        SettingsStore<AppSettings>.Changed += OnSettingsChangedExternally;
        // Эндпоинт сменился в настройках — реакция чата: CanExecute SendCommand.
        Settings.EndpointChanged += () => SendCommand.NotifyCanExecuteChanged();
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
            var endpoint = S.CompanionEndpoint;
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

    /// <summary>
    /// Настройки изменились извне (инструмент set_setting агента): живой экземпляр уже обновлён
    /// и записан на диск, остаётся перерисовать биндинг. Событие приходит из agent-потока —
    /// WPF-биндинг требует уведомлений на UI-потоке, поэтому маришализуем на Dispatcher.
    /// </summary>
    private void OnSettingsChangedExternally(AppSettings _) {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) {
            RefreshSettingsBindings();
        } else {
            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(RefreshSettingsBindings));
        }
    }

    /// <summary>
    /// Перерисовать биндинги настроек после внешнего изменения: зеркала — SettingsViewModel
    /// (рефлексия по совпадению имени с AppSettings — новое поле подхватится само),
    /// остальное — чат (ReasoningEffortIndex живёт здесь, превью промпта).
    /// </summary>
    private void RefreshSettingsBindings() {
        Settings.RefreshAll();
        OnPropertyChanged(nameof(ReasoningEffortIndex));
        RefreshPromptPreview();
    }

    // ── Профили чата: резолверы хода и диалог настройки (шестерёнка) ────────────────

    /// <summary>
    /// Единый с превью и ходом источник системного промпта: main-сессия — динамическая
    /// идентичность, специализированная — кусок-промпт из статичного хранилища профилей.
    /// </summary>
    /// <summary>
    /// Меню полок в тулбаре чата (🗄 + Popup): состояние, переключение, реакция на
    /// ShelfState.Deactivated. Состояние меню синхронизируется из shelves.json: при
    /// открытии меню, смене сессии, переключении и на каждый запрос (подхватывает
    /// переключения тулами агента).
    /// </summary>
    public ShelfUiViewModel Shelves { get; }

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
        if (SessionList.IsMainSession || IsGenerating)
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
    /// Сохранить текущую сессию (chat.json) — после каждого изменения чата. Список
    /// сессий обновляет SessionList (заголовок/время могли измениться).
    /// </summary>
    public void SaveCurrent() => SessionList.SaveCurrent();

    /// <summary>
    /// Сессия сменилась (событие SessionController): обновить вид — список/выбор/флаг
    /// main (в SessionList), превью, меню полок (полки per-session).
    /// </summary>
    private void OnSessionChanged() {
        SessionList.Refresh();
        RefreshPromptPreview();
        Shelves.Refresh();
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

    private bool CanSend() => !IsBusy && (InputText.Trim().Length > 0 || PendingAttachments.Count > 0) && S.Endpoint.Trim().Length > 0;

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
    private int EffectiveContextSize => Math.Min(S.ContextSize, _main.ServerProps.NContext ?? S.ContextSize);
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
        S.CompanionEndpoint, _main.Turns.ActiveToken);
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
            var companion = S.CompanionEndpoint;
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
