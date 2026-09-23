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
    private readonly bool _eraseSlotOnClose;

    private ChatWindow(ChatViewModel chat, Main? main, int slotId, string? sessionId, bool eraseSlotOnClose) {
        InitializeComponent();
        _chat = chat;
        _main = main;
        _slotId = slotId;
        _sessionId = sessionId;
        _eraseSlotOnClose = eraseSlotOnClose;
        DataContext = new ChatWindowViewModel(chat);
        // Закрываем окно — сохраняем разговор (своя сессия, SavePinned). Слот вычищаем
        // только если окно — единственный хозяин чата; у popout-окна чат живёт в пузыре
        // tool call (слот чистит спавнер в конце хода субагента).
        Closing += (_, _) => {
            _chat.SaveCurrent();
            if (_eraseSlotOnClose) EraseSlot();
        };
    }

    /// <summary>VM окна (для spawn-флоу: ввод задачи + запуск хода + чтение отчёта).</summary>
    public ChatViewModel Chat => _chat;

    /// <summary>
    /// Создать pinned-чат БЕЗ окна: VM + рантайм + хуки (профиль «subagent», слот
    /// SlotAllocation.Subagent). Чат живёт в пузыре tool call (встроенный UI); окно —
    /// опциональный popout на той же VM (CreateWindowFor). existingSessionId — загрузка
    /// истории с диска (reopening на сессии).
    /// </summary>
    public static (ChatViewModel Chat, string SessionId) CreatePinnedChat(
        Main main, Action shutdownApp, int slotId, string? promptKey, string? existingSessionId = null) {
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
        // История с диска (reopening). ПОСЛЕ создания VM: ReplaceAll шлёт Log.Changed, а
        // подписчик (RebuildMessageViews) появляется только в конструкторе VM — до него
        // загрузка осталась бы невидимой (пустой чат).
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
        return (chat, sessionId);
    }

    /// <summary>
    /// Окно-POPOUT на существующей VM (чат живёт в пузыре tool call; окно — отдельный
    /// вид того же разговора). Закрытие окна не убивает чат и не чистит слот.
    /// </summary>
    public static ChatWindow CreateWindowFor(ChatViewModel chat, Main main, int slotId, string sessionId, string title) {
        return new ChatWindow(chat, main, slotId, sessionId, eraseSlotOnClose: false) { Title = title };
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
