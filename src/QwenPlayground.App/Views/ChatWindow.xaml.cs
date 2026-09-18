using System.Windows;
using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Main;

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
/// </summary>
public partial class ChatWindow : Window {
    private readonly ChatViewModel _chat;

    private ChatWindow(ChatViewModel chat) {
        InitializeComponent();
        _chat = chat;
        DataContext = new ChatWindowViewModel(chat);
        Title = "Чат — своё окно";
        // Закрываем окно — сохраняем разговор (своя сессия, SavePinned).
        Closing += (_, _) => _chat.SaveCurrent();
    }

    /// <summary>
    /// Создать окно: новая закреплённая сессия + рантайм + VM. Хуки ссылаются на chat
    /// лениво (рантайм собирается до VM, хуки срабатывают только после конструирования).
    /// Heartbeat-хуки — no-op: субагент не будит main-агента.
    /// </summary>
    public static ChatWindow Create(Main main, Action shutdownApp) {
        ChatViewModel? chat = null;
        var hooks = new UiHooks(
            status => { if (chat is not null) chat.StatusText = status; },
            generating => { if (chat is not null) chat.IsGenerating = generating; },
            () => chat?.InputText ?? string.Empty,
            text => { if (chat is not null) chat.InputText = text; },
            () => chat?.SaveCurrent(),
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            () => chat?.Shelves.Refresh(),
            shutdownApp);
        var sessionId = main.Sessions.CreateDetached();
        var runtime = main.CreatePinnedRuntime(sessionId, hooks);
        chat = new ChatViewModel(runtime, main.Sessions, main.Heartbeat, main.Background,
            scheduleSettingsSave: () => { }, goToSettings: () => { }) { Pinned = true };
        chat.Initialize();
        return new ChatWindow(chat);
    }
}
