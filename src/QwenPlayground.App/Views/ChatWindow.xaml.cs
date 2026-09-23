using System.Windows;
using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Main;
using QwenPlayground.Core.Runtime;

namespace QwenPlayground.App.Views;

/// <summary>
/// DataContext отдельного окна чата: обёртка над ChatViewModel (IChatHost) — биндинги
/// ChatView в виде <c>DataContext.Chat.*</c> (RelativeSource AncestorType=Window) работают
/// и в главном окне (MainViewModel), и здесь.
/// </summary>
public sealed class ChatWindowViewModel : IChatHost {
    public ChatViewModel Chat { get; }

    public ChatWindowViewModel(ChatViewModel chat) => Chat = chat;
}

/// <summary>
/// Отдельное окно чата (мультиоконный квест, стадия C): закреплённая сессия (CreateDetached),
/// свой рантайм (CreatePinnedRuntime — общие сервисы шарятся с приложением), свой
/// ChatViewModel (Pinned). MainWindow — композиционный корень; окно самодостаточно.
///
/// Слот закреплён (SlotAllocation): ручное окно → 2. При закрытии окно вычищает свой
/// слот (KV больше не нужен — разговор сохранён в chat.json, продолжение начнётся
/// с пере-евалюацией промпта).
/// </summary>
public partial class ChatWindow : Window {
    private readonly ChatViewModel _chat;
    private readonly Main? _main;
    private readonly int _slotId;
    private readonly string? _sessionId;

    private ChatWindow(ChatViewModel chat, Main? main, int slotId, string? sessionId) {
        InitializeComponent();
        _chat = chat;
        _main = main;
        _slotId = slotId;
        _sessionId = sessionId;
        DataContext = new ChatWindowViewModel(chat);
        Title = "Чат — своё окно";
        // Закрываем окно — сохраняем разговор (своя сессия, SavePinned) и вычищаем слот.
        // Субагент: состояние НЕ снимаем — кнопка «Субагент» остаётся (субагент живёт
        // в процессе), окно можно открыть заново на той же сессии (reopening).
        Closing += (_, _) => {
            _chat.SaveCurrent();
            EraseSlot();
        };
    }

    /// <summary>VM окна (для spawn-флоу: ввод задачи + запуск хода + чтение отчёта).</summary>
    public ChatViewModel Chat => _chat;

    /// <summary>
    /// Создать окно: новая закреплённая сессия + рантайм + VM. Хуки ссылаются на chat
    /// лениво (рантайм собирается до VM, хуки срабатывают только после конструирования).
    /// Heartbeat-хуки — no-op: субагент не будит main-агента.
    /// </summary>
    public static ChatWindow Create(Main main, Action shutdownApp) {
        return CreateCore(main, shutdownApp, slotId: SlotAllocation.SideWindow,
            promptKey: null, title: "Чат — своё окно");
    }

    /// <summary>
    /// Окно субагента (spawn_subagent): профиль «subagent» (своя идентичность/контракт/
    /// DeniedTools), слот SlotAllocation.Subagent. Заголовок — из названия задачи.
    /// </summary>
    public static ChatWindow CreateSubagent(Main main, Action shutdownApp, string? title) {
        return CreateCore(main, shutdownApp, slotId: SlotAllocation.Subagent,
            promptKey: ChatProfileSet.SubagentPromptKey, title);
    }

    /// <summary>
    /// Reopening окна субагента на уже существующей сессии (после закрытия крестиком):
    /// история загружается с диска (сессия сохранена при закрытии).
    /// </summary>
    public static ChatWindow CreateSubagentReopen(Main main, Action shutdownApp, string sessionId, string title) {
        return CreateCore(main, shutdownApp, slotId: SlotAllocation.Subagent,
            promptKey: ChatProfileSet.SubagentPromptKey, title, existingSessionId: sessionId);
    }

    private static ChatWindow CreateCore(Main main, Action shutdownApp, int slotId, string? promptKey, string? title,
        string? existingSessionId = null) {
        ChatViewModel? chat = null;
        var hooks = new UiHooks(
            status => { if (chat is not null) chat.StatusText = status; },
            generating => {
                if (chat is not null) chat.IsGenerating = generating;
                // Субагент: состояние «работает/завершён» для кнопки в тулбаре.
                if (slotId == SlotAllocation.Subagent) main.Subagents.SetRunning(generating);
            },
            () => chat?.InputText ?? string.Empty,
            text => { if (chat is not null) chat.InputText = text; },
            () => chat?.SaveCurrent(),
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            () => chat?.Shelves.Refresh(),
            shutdownApp);
        var sessionId = existingSessionId ?? main.Sessions.CreateDetached();
        var runtime = main.CreatePinnedRuntime(sessionId, hooks, promptKey: promptKey, slotId: slotId);
        chat = new ChatViewModel(runtime, main.Sessions, main.Heartbeat, main.Background,
            scheduleSettingsSave: () => { }, goToSettings: () => { }) { Pinned = true };
        // Reopening: история с диска (сессия сохранена при закрытии окна). ПОСЛЕ создания VM:
        // ReplaceAll шлёт Log.Changed, а подписчик (RebuildMessageViews) появляется только
        // в конструкторе VM — до него загрузка осталась бы невидимой (пустое окно).
        if (existingSessionId is not null)
        {
            var data = main.Sessions.LoadData(existingSessionId);
            if (data is not null)
            {
                runtime.Log.ReplaceAll(data.Messages);
                runtime.Log.SetNextMessageId(data.NextMessageId);
            }
        }
        chat.Initialize();
        // title — сырой (без префикса): префикс «Субагент —» — только у окна субагента;
        // состояние для кнопки хранит сырой title (тултип «Субагент работает: {title}»).
        var window = new ChatWindow(chat, main, slotId, sessionId) {
            Title = slotId == SlotAllocation.Subagent
                ? (string.IsNullOrWhiteSpace(title) ? "Субагент" : $"Субагент — {title}")
                : title
        };
        // Субагент: состояние для кнопки в тулбаре (живёт, пока субагент в процессе).
        // Храним RAW-название (без префикса «Субагент — »): reopening собирает заголовок
        // окна сам, а тултип читается «Субагент работает: {title}» (без дубля префикса).
        if (slotId == SlotAllocation.Subagent)
        {
            const string prefix = "Субагент — ";
            var rawTitle = string.IsNullOrEmpty(title)
                ? string.Empty
                : title.StartsWith(prefix, StringComparison.Ordinal) ? title[prefix.Length..] : title;
            main.Subagents.SetCurrent(sessionId, rawTitle, isRunning: existingSessionId is null);
        }
        return window;
    }

    /// <summary>
    /// Вычистить KV слота окна (fire-and-forget): слот больше не нужен, GPU освобождаем.
    /// Ошибки не критичны (слот с медиа, сервер без флага) — молча пропускаем.
    /// </summary>
    private void EraseSlot() {
        if (_main is null) return;
        _ = _main.KvCache.EraseSlotAsync(_slotId).ContinueWith(_ => { });
    }
}
