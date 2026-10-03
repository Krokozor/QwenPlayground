using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Heartbeat;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Main;
using QwenPlayground.Core.Subagents;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Runtime;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Templates;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Хост окна чата: DataContext, содержащий ChatViewModel. MainViewModel (главное окно)
/// и ChatWindowViewModel (отдельное окно чата, стадия C) — оба реализуют; code-behind
/// ChatView резолвит Chat через этот интерфейс, не зная про конкретного хоста.
/// </summary>
public interface IChatHost {
    ChatViewModel Chat { get; }
}

/// <summary>
/// Окно чата: ядро разговора (сообщения, ввод, отправка, превью промпта, статус) и его
/// LEGO-модули (панель сессий, меню полок, команды сообщений, проекция хода). Стадия A
/// мультиоконного квеста: самодостаточный элемент, который можно хостить в любом окне.
/// Композиционный корень приложения (MainViewModel) держит lifecycle, настройки и прочие
/// вкладки. Зависимости — рантайм чата (Log/FSM/Turns/...) + три общих сервиса
/// (Sessions, Heartbeat, Background) и два делегата (отложенный save настроек, переход
/// на вкладку настроек из шестерёнки чата).
///
/// Pinned (окно субагента, стадия C): рантайм закреплён за своей сессией — селектор
/// сессий, wake и настройка чата скрыты, сейв идёт через SavePinned.
/// </summary>
public partial class ChatViewModel : ObservableObject {
    private readonly ChatRuntime _runtime;
    private readonly SessionController _sessions;
    private readonly HeartbeatController _heartbeat;
    private readonly BackgroundWork _background;
    private readonly Action _scheduleSettingsSave;
    private readonly Action _goToSettings;

    /// <summary>Закреплённое окно (субагент): своя сессия, селектор сессий скрыт.</summary>
    public bool Pinned { get; init; }

    /// <summary>
    /// Агент требует внимания: карточка подтверждения появилась в этом чате. Хост окна
    /// подписывается и фокусирует себя (настройка FocusOnConfirmation). Срабатывает
    /// только когда карточка реально показана (не в YOLO-пути — там кликать нечего).
    /// </summary>
    public event Action? AttentionRequired;

    /// <summary>Разговор (read-доступ для программных потребителей: отчёт субагента в spawn-флоу).</summary>
    public ChatLog Log => _runtime.Log;

    /// <summary>Селектор сессий + wake + настройка чата: только в главном окне.</summary>
    public bool ShowSessionPicker => !Pinned;

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
    /// «Отправить при следующей возможности» вооружено: во время хода кнопка «Отправить»
    /// — тоггл буфера следующего хода. Текст остаётся в окне ввода (редактируемый), а
    /// когда ход завершится (IsGenerating → false), OnIsGeneratingChanged сам запустит
    /// отправку. Снятие: повторный клик по кнопке, окно ввода опустело, смена сессии
    /// или очистка разговора.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _queueArmed;

    /// <summary>
    /// Ход отменяется (кнопка «Стоп»): тогда OnIsGeneratingChanged(false) НЕ должен
    /// отправлять queued-сообщение — пользователь ждал прекращения и генерации, и
    /// отправки. Сбрасывается в начале нового хода.
    /// </summary>
    private bool _cancelRequested;

    /// <summary>
    /// Подпись кнопки «Отправить»: «Отправить» (хода нет) / «➤ В очередь» и
    /// «✕ Отменить очередь» (идёт ход — кнопка тоггл очереди).
    /// </summary>
    public string SendButtonText => !IsGenerating
        ? "Отправить"
        : QueueArmed ? "✕ Отменить" : "➤ В очередь";

    /// <summary>Подсказка кнопки: режим меняется вместе с ходом.</summary>
    public string SendButtonToolTip => !IsGenerating
        ? "Отправить сообщение"
        : QueueArmed
            ? "Снять с очереди: текст останется в окне ввода"
            : "Поставить в очередь: сообщение влетит в разговор после ближайших инструментов, до следующего моего ответа";

    /// <summary>
    /// Команды сообщений живут в MessageCommands — их CanExecute (Reroll/Continue)
    /// зависит от IsGenerating: уведомляем модуль, когда флаг переключается.
    /// Конец хода + вооружённая очередь — «следующая возможность»: отправляем
    /// queued-сообщение (текст и вложения читаются в момент отправки — пользователь
    /// мог отредактировать их, пока ход шёл).
    /// </summary>
    partial void OnIsGeneratingChanged(bool value) {
        MessageCommands?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SendButtonText));
        OnPropertyChanged(nameof(SendButtonToolTip));
        if (value) {
            // Новый ход — флаг отмены предыдущего хода больше не актуален.
            _cancelRequested = false;
            return;
        }
        // Ход завершён (IsGenerating → false).
        if (_cancelRequested) {
            // Ход отменён (Стоп): queued-сообщение НЕ отправляем — пользователь ждал
            // прекращения и генерации, и отправки. Очередь снимаем, текст остаётся
            // в окне ввода (его можно отредактировать и отправить вручную).
            _cancelRequested = false;
            if (QueueArmed) {
                QueueArmed = false;
                StatusText = "Ход отменён: очередь снята, текст остался в окне ввода";
            }
            return;
        }
        // Нормальное завершение хода — «следующая возможность»: отправляем queued
        // (текст и вложения читаются в момент отправки — пользователь мог
        // отредактировать их, пока ход шёл).
        if (!QueueArmed) {
            return;
        }
        QueueArmed = false;
        _ = SendQueuedAsync();
    }

    partial void OnQueueArmedChanged(bool value) {
        OnPropertyChanged(nameof(SendButtonText));
        OnPropertyChanged(nameof(SendButtonToolTip));
    }

    partial void OnInputTextChanged(string value) {
        // Очередь без контента бессмысленна: окно ввода опустело (и вложений нет) —
        // вооружение снимается само (состояние читается по подписи кнопки).
        if (QueueArmed && string.IsNullOrWhiteSpace(value) && PendingAttachments.Count == 0) {
            QueueArmed = false;
        }
    }

    /// <summary>Чат занят (нельзя принимать новые ходы/ручную компакцию). Вычисляется из FSM.</summary>
    public bool IsBusy => _runtime.ChatState.IsBusy;

    private bool CanInteract() => !IsBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _promptPreview = string.Empty;

    public ObservableCollection<MessageViewModel> Messages { get; } = new();

    /// <summary>
    /// Прикреплённые к следующему сообщению файлы (картинки и т.п.). Копируются в
    /// artifacts/msg_&lt;id&gt;/ при отправке и уходят как multimodal_data (маркер + base64),
    /// а не текстовым мусором.
    /// </summary>
    public ObservableCollection<PendingAttachment> PendingAttachments { get; } = new();

    /// <summary>Живое превью компакции (панель, стадии, стриминг токенов).</summary>
    public CompactionPreview Compaction => _runtime.Compaction;

    public ReasoningEffort ReasoningEffort {
        get => S.ReasoningEffort;
        set {
            // Живёт здесь (не в SettingsViewModel): биндинг тулбара чата + реакция превью.
            var old = S.ReasoningEffort;
            S.ReasoningEffort = value;
            if (old != value) {
                _scheduleSettingsSave();
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

    // ── LEGO-модули окна чата ────────────────────────────────────────────────────────

    /// <summary>
    /// Панель сессий в тулбаре чата (селектор + «+»/«×»): список, выбор, команды.
    /// Логика — в SessionController (Core); реакции на смену — в OnSessionChanged.
    /// </summary>
    public SessionListViewModel SessionList { get; }

    /// <summary>
    /// Команды операций над сообщениями: откат, просмотр промпта, редактирование,
    /// копирование, реколл/продолжение, вложения. Коллекции и состояние чата — здесь;
    /// в модуль приходят ссылки и делегаты.
    /// </summary>
    public MessageCommandsViewModel MessageCommands { get; }

    /// <summary>
    /// Проекция вида хода: события TurnPipeline → пузыри, статус, живые реколлы.
    /// Доменная оркестрация — в TurnPipeline (Core).
    /// </summary>
    public TurnViewViewModel TurnView { get; }

    /// <summary>
    /// Меню полок в тулбаре чата (🗄 + Popup): состояние, переключение, реакция на
    /// ShelfState.Deactivated. Состояние меню синхронизируется из shelves.json: при
    /// открытии меню, смене сессии, переключении и на каждый запрос.
    /// </summary>
    public ShelfUiViewModel Shelves { get; }

    /// <summary>
    /// Панель TODO в тулбаре чата (кнопка «TODO» + сворачиваемая панель): ручной просмотр/
    /// правка TODO-списка текущей сессии (sessions/&lt;id&gt;/TODO.json — тот же файл, что и у
    /// тулов TODO_add/TODO_manage; правки идут с источником «user»).
    /// </summary>
    public TodoViewModel Todo { get; }

    public ChatViewModel(
        ChatRuntime runtime,
        SessionController sessions,
        HeartbeatController heartbeat,
        BackgroundWork background,
        Action scheduleSettingsSave,
        Action goToSettings,
        KvCacheController kv,
        SubagentSpawner subagents) {
        _runtime = runtime;
        _sessions = sessions;
        _heartbeat = heartbeat;
        _background = background;
        _scheduleSettingsSave = scheduleSettingsSave;
        _goToSettings = goToSettings;

        Shelves = new(() => SessionDir());
        Todo = new(() => SessionDir());
        // Общий KV-контроллер и спавнер (фаза 3, план 2026-09-28): панель сессий не
        // конструирует свои new — слоты и occupancy идут через сервисы Main.
        SessionList = new(_sessions, _runtime.Log, () => IsGenerating, status => StatusText = status, kv, subagents);
        TurnView = new(_runtime, _background, Messages, SessionDir, status => StatusText = status);
        MessageCommands = new(Messages, PendingAttachments, _runtime.Log,
            CanInteract, () => IsGenerating, continueLast => TurnView.GenerateAsync(continueLast),
            SaveCurrent, RefreshPromptPreview, status => StatusText = status);

        _runtime.Log.Changed += OnLogChanged;
        _sessions.SessionChanged += OnSessionChanged;
        // Снятие полки (тулом агента или из меню) — доменное событие; реакция UI — в меню.
        ShelfState.Deactivated += Shelves.OnDeactivated;

        Messages.CollectionChanged += (_, _) => {
            MessageCommands.RerollCommand.NotifyCanExecuteChanged();
            MessageCommands.ContinueCommand.NotifyCanExecuteChanged();
            RefreshPromptPreview();
        };

        // Вложения к следующему сообщению: SendCommand.canexec меняется (можно отправить
        // и картинку без текста) + чипсы в UI. Очередь снимается, если контент стал пустым.
        PendingAttachments.CollectionChanged += (_, _) => {
            SendCommand.NotifyCanExecuteChanged();
            if (QueueArmed && PendingAttachments.Count == 0 && InputText.Trim().Length == 0) {
                QueueArmed = false;
            }
        };
    }

    /// <summary>
    /// Старт чата: main-сессия, последняя открытая, драфт в окошко ввода, превью.
    /// Вызывается композиционным корнем ПОСЛЕ присваивания свойства Chat: Core во время
    /// RestoreLast колбэкает хук ввода (DraftKeeper.Flush → getInputText), а хуки идут
    /// через Chat — он уже должен быть достижим (в конструкторе Chat ещё null).
    /// </summary>
    public void Initialize() {
        if (Pinned) {
            // Закреплённое окно: сессия своя, main/restore не участвуют; драфт не тащим
            // (окно не переживает рестарт — черновик не нужен).
            RefreshPromptPreview();
            return;
        }
        StartupTrace.Log("ChatViewModel Initialize: PruneEmptySessionFolders");
        // Уборка наследия: каталоги сессий создавались и на чтении, поэтому удалённые и
        // так и не сохранённые сессии оставляли после себя пустые sessions/<id>/.
        // До любых реакций вида — иначе Refresh/Shelves успеют наплодить новых.
        _sessions.PruneEmptyFolders();
        StartupTrace.Log("ChatViewModel Initialize: EnsureMain");
        SessionList.EnsureMain();
        StartupTrace.Log("ChatViewModel Initialize: RestoreLast");
        SessionList.RestoreLast();
        // Восстановить драфт ТЕКУЩЕЙ сессии (main или последней открытой) в окошко ввода:
        // переживает обрыв питания/крах — набранный промпт возвращается.
        _runtime.Draft.Restore();
        StartupTrace.Log("ChatViewModel Initialize: RefreshPromptPreview (startup)");
        RefreshPromptPreview();
    }

    /// <summary>Каталог сессии рантайма: у каждой сессии своя папка sessions/&lt;id&gt;/ (как у main-агента).</summary>
    private string SessionDir() => _sessions.DirectoryFor(_runtime.SessionId());

    // Структурные изменения разговора (компакция/загрузка/откат) сами перестраивают вид.
    private void OnLogChanged() => RebuildMessageViews();

    /// <summary>Окно старта (120 с): в нём пер-сообщение трейс — диагностика зависаний
    /// «на каком сообщении встал RebuildMessageViews» (баг handshake timeout). После — тишина.</summary>
    private static readonly DateTime ProcessStartAt = DateTime.Now;

    public void RebuildMessageViews() {
        var sw = Stopwatch.StartNew();
        var count = _runtime.Log.Count;
        var sessionDir = SessionDir();
        Messages.Clear();
        var added = 0;
        var startupWindow = DateTime.Now - ProcessStartAt < TimeSpan.FromSeconds(120);
        foreach (var message in _runtime.Log) {
            if (startupWindow) {
                StartupTrace.Log($"RebuildMessageViews: +msg {message.Id} {message.Role}");
            }
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

    [RelayCommand(CanExecute = nameof(CanSend), AllowConcurrentExecutions = true)]
    private async Task SendAsync() {
        if (IsGenerating) {
            // Идёт ход: кнопка — тоггл «отправить при следующей возможности». Текст
            // остаётся в окне ввода (редактируемый); отправку запустит
            // OnIsGeneratingChanged(false) в конце хода.
            QueueArmed = !QueueArmed;
            StatusText = QueueArmed
                ? "Сообщение в очереди: влетит в разговор после ближайших инструментов — до следующего моего ответа (кнопка — отмена)"
                : "Очередь отменена: текст остался в окне ввода";
            return;
        }
        await SendNowAsync();
    }

    /// <summary>
    /// Отправка сообщения — единый путь: ручная кнопка (холостой чат) и queued-отправка
    /// после хода. Текст и вложения читаются в момент вызова.
    /// </summary>
    private async Task SendNowAsync() {
        var text = InputText.Trim();
        InputText = string.Empty;
        // Текст отправлен (стал сообщением) — драфт удаляем, чтобы не восстанавливать
        // отправленное при следующем старте.
        _runtime.Draft.ClearOnSend();
        var userMessage = ChatMessage.User(text);
        _runtime.Log.Add(userMessage); // ID присваивается здесь же — до копирования вложений
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
        await TurnView.GenerateAsync();
        SaveCurrent();
    }

    /// <summary>
    /// Провайдер queued-сообщения для агентного цикла (mid-turn инъекция, «отправить при
    /// следующей возможности»): если очередь вооружена и есть контент — создаёт
    /// user-сообщение, добавляет его в разговор (ID присваивается, вложения пришиваются
    /// тем же путём, что и при ручной отправке), снимает вооружение и очищает ввод,
    /// возвращает сообщение. Агентный цикл вставляет его в разговор перед следующим
    /// вызовом модели (после инструментов текущей итерации). null — очереди нет.
    /// Вызывается на UI-потоке (агентный цикл живёт на потоке UI).
    /// </summary>
    public ChatMessage? GetQueuedMessage() {
        if (!QueueArmed) {
            return null;
        }
        var text = InputText.Trim();
        var hasAttachments = PendingAttachments.Count > 0;
        if (text.Length == 0 && !hasAttachments) {
            // Пустая очередь — снимаем вооружение, сообщения нет.
            QueueArmed = false;
            return null;
        }
        // Сначала снимаем вооружение, потом очищаем ввод (чтобы OnInputTextChanged
        // не пытался снять уже снятую очередь).
        QueueArmed = false;
        var userMessage = ChatMessage.User(text);
        _runtime.Log.Add(userMessage); // ID присваивается здесь же
        // Вложения — тот же путь, что и при ручной отправке (SendNowAsync).
        var attachments = PendingAttachments.ToList();
        PendingAttachments.Clear();
        InputText = string.Empty;
        _runtime.Draft.ClearOnSend();
        var metaStore = new MessageMetaStore(SessionDir());
        var announcedPaths = new List<string>();
        foreach (var attachment in attachments) {
            try {
                if (attachment.IsImage) {
                    metaStore.AddArtifact(userMessage.Id, attachment.FullPath);
                } else {
                    announcedPaths.Add(metaStore.AddFileArtifact(userMessage.Id, attachment.FullPath));
                }
            } catch {
                // файл не прочитался — пропускаем (как в SendNowAsync).
            }
        }
        if (announcedPaths.Count > 0) {
            var tags = string.Join("\n", announcedPaths.Select(p => $"<attachment path=\"{ToWorkspaceRelative(p)}\">"));
            userMessage.Content = (userMessage.Content + "\n" + tags).Trim();
        }
        // UI-рендер: явно добавляем MessageViewModel в Messages (как в SendNowAsync).
        // Log.Changed → RebuildMessageViews не срабатывает при добавлении из агентного
        // цикла, поэтому без явного Add сообщение не появится в UI до рестарта.
        var userView = MessageViewModel.FromMessage("user", userMessage);
        userView.LoadArtifacts(SessionDir());
        Messages.Add(userView);
        StatusText = "Сообщение из очереди вставлено в разговор";
        return userMessage;
    }

    /// <summary>Путь относительно корня workspace (для read_file и тега &lt;attachment&gt;).</summary>
    private static string ToWorkspaceRelative(string path) {
        var wsRoot = SelfBuildPaths.WorkspaceRoot;
        return path.StartsWith(wsRoot, StringComparison.OrdinalIgnoreCase)
            ? path[wsRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;
    }

    private bool CanSend() {
        // Кнопка неактивна ТОЛЬКО когда нет ни текста, ни вложений. Остальные проверки
        // (endpoint, ход) — в момент реальной отправки, не на уровне CanExecute.
        return InputText.Trim().Length > 0 || PendingAttachments.Count > 0;
    }

    /// <summary>
    /// Отправка queued-сообщения в конце хода (fire-and-forget из OnIsGeneratingChanged):
    /// тот же путь, что и ручная отправка; ошибка — в статусную строку, не в краш.
    /// </summary>
    private async Task SendQueuedAsync() {
        if (InputText.Trim().Length == 0 && PendingAttachments.Count == 0) {
            return;
        }
        try {
            await SendNowAsync();
        }
        catch (Exception exception) {
            StatusText = $"ошибка отправки queued-сообщения: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsGenerating))]
    private void Cancel() {
        // Помечаем отмену: когда IsGenerating станет false, OnIsGeneratingChanged
        // не отправит queued-сообщение (а снимет очередь).
        _cancelRequested = true;
        _runtime.Turns.Cancel();
    }

    /// <summary>
    /// Очистка текущего разговора — программный доступ (Harness). UI-кнопка «Очистить»
    /// убрана (2026-09-02): сценарий покрывает «откат» первого сообщения.
    /// </summary>
    public void Clear() {
        QueueArmed = false; // очередь привязана к текущему тексту — с разговором ушла
        _runtime.Log.Clear();
        StatusText = string.Empty;
        SaveCurrent();
    }

    /// <summary>Ручная компакция из UI.</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task CompactAsync() => await _runtime.Maintenance.CompactFromUiAsync();

    /// <summary>Скрыть панель live-превью сжатия («×» на панели).</summary>
    [RelayCommand]
    private void HideCompactionPanel() => _runtime.Compaction.Hide();

    /// <summary>Открыть панель сжатия из тулбара (кнопка активна, когда в панели есть что показывать).</summary>
    [RelayCommand]
    private void ShowCompactionPanel() => _runtime.Compaction.Open();

    /// <summary>Ручной wake (кнопка): если есть сигнал — обработать его, иначе обычный heartbeat.</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void WakeNow() => _heartbeat.WakeNow();

    /// <summary>
    /// Подтверждение опасной команды: КАРТОЧКА в этом чате, а не модалка (фаза 1.5,
    /// план 2026-09-28). Карточка появляется в разговоре окна, которое идёт ход,
    /// ход ждёт решения (FSM AwaitingConfirmation держит провайдер), UI-поток свободен
    /// (нет ShowDialog и его вложенного message loop). После решения карточка остаётся
    /// в истории с исходом (аудит); в chat.json попадает результат тула.
    /// YOLO-режим: карточки нет — авто-разрешение + запись в events-лог.
    /// </summary>
    public Task<bool> RequestConfirmationAsync(string question, CancellationToken cancellationToken)
    {
        if (S.YoloMode)
        {
            var snippet = question.Length > 200 ? question[..200] + "…" : question;
            AppEventLog.Log($"YOLO: авто-разрешена опасная команда: {snippet}");
            return Task.FromResult(true);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(false);
        }
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var message = MessageViewModel.Confirmation(question);
        var registration = cancellationToken.Register(() => message.ResolveCancelled());
        message.ConfirmResolve = allowed =>
        {
            registration.Dispose();
            tcs.TrySetResult(allowed);
        };
        Messages.Add(message);
        if (S.FocusOnConfirmation)
        {
            AttentionRequired?.Invoke();
        }
        return tcs.Task;
    }

    /// <summary>Ход main-агента по инициативе приложения: всегда агентный режим (иначе бессмысленно).</summary>
    public async Task RunHeartbeatTurnAsync(string prompt) {
        // Busy-чек ПЕРЕД записью сообщения: ход, который не стартовал, не должен оставлять
        // [heartbeat]/[wake]-зомби в разговоре — модель увидит его в следующем промпте.
        if (IsBusy) {
            return;
        }
        var userMessage = ChatMessage.User(prompt);
        _runtime.Log.Add(userMessage);
        Messages.Add(MessageViewModel.FromMessage("user", userMessage));
        await TurnView.GenerateAsync();
        SaveCurrent();
    }

    // ── Профили чата: резолверы хода и диалог настройки (шестерёнка) ────────────────

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
            _sessions.SamplerKey, _sessions.PromptKey, _sessions.StateBlockKey)
        { Owner = System.Windows.Application.Current.MainWindow };
        // Пресеты редактируются в ЕДИНСТВЕННОМ месте — вкладка «Настройки»; диалог закрываем.
        dialog.GoToSettings = _goToSettings;
        if (dialog.ShowDialog() == true) {
            _sessions.ApplyProfileKeys(
                dialog.SelectedSamplerKey, dialog.SelectedPromptKey, dialog.SelectedStateBlockKey);
            RefreshPromptPreview();
            StatusText = "Настройка чата применена: действует со следующего хода.";
        }
    }

    private static List<string> OrderedKeys(IEnumerable<string> keys) =>
        keys.OrderBy(k => k == ChatProfileSet.DefaultKey ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Сохранить разговор — после каждого изменения чата: главное окно — текущую сессию
    /// (chat.json, список обновляет SessionList); закреплённое — свою (SavePinned).
    /// </summary>
    public void SaveCurrent() {
        if (Pinned) {
            _sessions.SavePinned(_runtime.SessionId(), _runtime.Log,
                _runtime.SamplerKey, _runtime.PromptKey, _runtime.StateBlockKey);
        } else {
            SessionList.SaveCurrent();
        }
    }

    /// <summary>
    /// Сессия сменилась (событие SessionController): обновить вид — список/выбор/флаг
    /// main (в SessionList), превью, меню полок (полки per-session).
    /// </summary>
    private void OnSessionChanged() {
        // Сессия сменилась: текст в окне уже драфт НОВОЙ сессии (Restore идёт до
        // события) — очередь, указывавшая на старый текст, бессмысленна.
        QueueArmed = false;
        SessionList.Refresh();
        RefreshPromptPreview();
        Shelves.Refresh();
        Todo.OnSessionChanged();
    }

    /// <summary>Внешнее уведомление биндинга ReasoningEffortIndex (настройки сменил тулом агент).</summary>
    public void NotifyReasoningEffortChanged() => OnPropertyChanged(nameof(ReasoningEffortIndex));

    private int _previewRenderCount;
    public void RefreshPromptPreview() {
        var sw = Stopwatch.StartNew();
        try {
            var preview = _runtime.Pipeline.RenderForPreview();
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
}
