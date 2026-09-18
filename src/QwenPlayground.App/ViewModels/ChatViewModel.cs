using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Heartbeat;
using QwenPlayground.Core.Main;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.MetaInfo;
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
    /// Команды сообщений живут в MessageCommands — их CanExecute (Reroll/Continue)
    /// зависит от IsGenerating: уведомляем модуль, когда флаг переключается.
    /// </summary>
    partial void OnIsGeneratingChanged(bool value) {
        MessageCommands?.NotifyCanExecuteChanged();
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

    public ChatViewModel(
        ChatRuntime runtime,
        SessionController sessions,
        HeartbeatController heartbeat,
        BackgroundWork background,
        Action scheduleSettingsSave,
        Action goToSettings) {
        _runtime = runtime;
        _sessions = sessions;
        _heartbeat = heartbeat;
        _background = background;
        _scheduleSettingsSave = scheduleSettingsSave;
        _goToSettings = goToSettings;

        Shelves = new(() => SessionDir());
        SessionList = new(_sessions, _runtime.Log, () => IsGenerating, status => StatusText = status);
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
        // и картинку без текста) + чипсы в UI.
        PendingAttachments.CollectionChanged += (_, _) => SendCommand.NotifyCanExecuteChanged();
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

    public void RebuildMessageViews() {
        var sw = Stopwatch.StartNew();
        var count = _runtime.Log.Count;
        var sessionDir = SessionDir();
        Messages.Clear();
        var added = 0;
        foreach (var message in _runtime.Log) {
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

    /// <summary>Путь относительно корня workspace (для read_file и тега &lt;attachment&gt;).</summary>
    private static string ToWorkspaceRelative(string path) {
        var wsRoot = SelfBuildPaths.WorkspaceRoot;
        return path.StartsWith(wsRoot, StringComparison.OrdinalIgnoreCase)
            ? path[wsRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;
    }

    private bool CanSend() => !IsBusy && (InputText.Trim().Length > 0 || PendingAttachments.Count > 0) && S.Endpoint.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(IsGenerating))]
    private void Cancel() => _runtime.Turns.Cancel();

    /// <summary>
    /// Очистка текущего разговора — программный доступ (Harness). UI-кнопка «Очистить»
    /// убрана (2026-09-02): сценарий покрывает «откат» первого сообщения.
    /// </summary>
    public void Clear() {
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

    /// <summary>Ход main-агента по инициативе приложения: всегда агентный режим (иначе бессмысленно).</summary>
    public async Task RunHeartbeatTurnAsync(string prompt) {
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
        SessionList.Refresh();
        RefreshPromptPreview();
        Shelves.Refresh();
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
