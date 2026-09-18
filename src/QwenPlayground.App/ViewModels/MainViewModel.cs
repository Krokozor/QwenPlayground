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

public partial class MainViewModel : ObservableObject, IChatHost {
    // Композиционный корень (Core/Main): граф сервисов собирается там, UI — протокол-адаптер
    // (пузыри, команды, тонкие виды). Хуки UI — через UiHooks в конструкторе.
    private readonly Main _main;

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

    /// <summary>
    /// Окно чата (стадия A мультиоконного квеста): ядро разговора (сообщения, ввод,
    /// отправка, превью, статус) + LEGO-модули (сессии, полки, команды, проекция хода).
    /// Хостится во вкладке «Чат» главного окна; самодостаточен для переноса в отдельное
    /// окно (стадии B–D).
    /// </summary>
    public ChatViewModel Chat { get; }

    /// <summary>Индекс вкладки «Настройки» в главном окне (для перехода из шестерёнки чата).</summary>
    public const int SettingsTabIndex = 2;

    [ObservableProperty]
    private int _selectedTabIndex;

    public MainViewModel() {
        StartupTrace.Log("MainViewModel ctor: begin");
        // Композиционный корень (Core/Main): граф сервисов собирается там (единственное
        // место, знающее порядок). Хуки UI — через UiHooks: фасад не знает про WPF.
        // Хуки чата ссылаются на Chat лямбдами (лениво): Chat создаётся ниже, хуки
        // срабатывают только после конструирования.
        _main = new Main(new UiHooks(
            status => Chat.StatusText = status,
            generating => Chat.IsGenerating = generating,
            () => Chat.InputText,
            text => Chat.InputText = text,
            () => Chat.SaveCurrent(),
            prompt => Chat.RunHeartbeatTurnAsync(prompt),
            FlushMemoryVectorsAsync,
            () => Chat.Shelves.Refresh(), // меню должно показать снятые полки
            () => System.Windows.Application.Current?.Shutdown()),
            typeof(AgentTool).Assembly, // Core: базовые инструменты
            typeof(MainViewModel).Assembly); // App: UI-инструменты (screenshot, switch_tab)
        // Окно чата: ядро разговора + LEGO-модули (сессии/полки/команды/проекция хода).
        // Старт чата — после присваивания: Core во время RestoreLast колбэкает хук ввода,
        // который идёт через Chat (в конструкторе Chat ещё null).
        Chat = new(_main.Runtime, _main.Sessions, _main.Heartbeat, _main.Background,
            Settings.ScheduleSave, () => SelectedTabIndex = SettingsTabIndex);
        Chat.Initialize();
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
        Settings.EndpointChanged += () => Chat.SendCommand.NotifyCanExecuteChanged();
        StartupTrace.Log("MainViewModel ctor: lifecycle StartAll");
        _main.Lifecycle.StartAll();
        StartupTrace.Log("MainViewModel ctor: done");

        // Саморебилд индикатор v2
        var lastBuild = StateBlockBuilder.LastBuild();

        if (lastBuild is not null) {
            Chat.StatusText = $"⚡ Сборка {lastBuild.Id} | Режим бога активирован";
        }

        // Контекст краха: каждая запись CrashLog несёт «что делалось в момент смерти»
        // (активные ходы, сессия, FSM) — картину не придётся собирать по кускам.
        CrashLog.AddContextProvider(BuildCrashContext);

        // Предыдущий запуск закончился крахом — не даём проскочить незаметно.
        if (File.Exists(CrashLog.LastCrashFile) &&
            (DateTime.Now - File.GetLastWriteTime(CrashLog.LastCrashFile)).TotalHours < 24) {
            Chat.StatusText = "⚠ предыдущий запуск закончился крахом — logs/last-crash.log";
        }
    }

    /// <summary>Снимок «что происходило» для записей CrashLog (вызывается синхронно, без блокировок).</summary>
    private string BuildCrashContext() {
        var sb = new StringBuilder();
        sb.AppendLine($"session: {_main.Sessions.CurrentId}");
        sb.AppendLine($"chat FSM: {_main.ChatState.Current}; generating: {Chat.IsGenerating}");
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

        Chat.RebuildMessageViews();
        Chat.SaveCurrent();
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
            _main.Background.Queue("продолжение цепочки tool", () => Chat.TurnView.GenerateAsync());
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
                Chat.StatusText = $"🧠 память: векторизовано {processed} фактов";
            }

            else {
                // Все факты с векторами — надмозг переключается на поиск дубликатов.
                var scan = await MemorySimilarity.ScanPassAsync(
                store, _main.PairsStore, endpoint, AppSettings.Get().MemoryScanProbeBudget,
                (prompt, _, _, ct) => LlmProbeClient.ProbeAsync(endpoint, prompt, nProbs: 20, ct),
                token);

                var pending = new PairsStore(store.Root).Pending.Count;

                if (scan.QueuedSimilar > 0) {
                    Chat.StatusText = $"🧠 память: {scan.QueuedSimilar} похожих фактов ждут разрешения (memory_manage)";
                }

                else if (pending > 0 && scan.Probes > 0) {
                    Chat.StatusText = $"🧠 память: скан дубликатов, пар в очереди: {pending}";
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
        Chat.NotifyReasoningEffortChanged();
        Chat.RefreshPromptPreview();
    }

    // ── Профили чата: резолверы хода и диалог настройки (шестерёнка) ────────────────

    /// <summary>
    /// Единый с превью и ходом источник системного промпта: main-сессия — динамическая
    /// идентичность, специализированная — кусок-промпт из статичного хранилища профилей.
    /// </summary>
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
    /// Эффективный размер окна: реальный n_ctx сервера (если известен), иначе настроенный
    /// ContextSize. Для проверки «влезет ли» сравниваем именно с ним — это то, что реально
    /// спрашивает сервер.
    /// </summary>
    private int EffectiveContextSize => Math.Min(S.ContextSize, _main.ServerProps.NContext ?? S.ContextSize);

    // ── Окна чата (мультиоконный квест, стадия C) ────────────────────────────────────

    /// <summary>
    /// Новое окно чата: закреплённая сессия (CreateDetached), свой рантайм (CreatePinnedRuntime),
    /// свой ChatViewModel (Pinned). Общие сервисы — из Main.
    /// </summary>
    [RelayCommand]
    private void OpenChatWindow() {
        var window = Views.ChatWindow.Create(_main, () => System.Windows.Application.Current?.Shutdown());
        window.Show();
    }
}
