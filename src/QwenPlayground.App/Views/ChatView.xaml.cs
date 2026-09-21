using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QwenPlayground.App.Desktop;
using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Compaction;

namespace QwenPlayground.App.Views;

public partial class ChatView : UserControl
{
    // Хост (MainViewModel — главное окно, ChatWindowViewModel — отдельное окно чата):
    // резолвим Chat через IChatHost, не привязываясь к конкретному DataContext.
    private ChatViewModel? _chat;
    private bool _stickToBottom = true;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        MessagesScroll.ScrollChanged += OnScrollChanged;

        // Desktop cursor position indicator
        DesktopOverlay.PositionChanged += UpdateDesktopCursorPos;
        Unloaded += (_, _) => DesktopOverlay.PositionChanged -= UpdateDesktopCursorPos;
    }

    private void UpdateDesktopCursorPos()
    {
        if (DesktopCursorPos is null) return;
        DesktopCursorPos.Text = $"({DesktopOverlay.AgentX}, {DesktopOverlay.AgentY})";
        DesktopCursorPos.Visibility =
            (DesktopOverlay.AgentX != 0 || DesktopOverlay.AgentY != 0)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_chat is not null)
        {
            _chat.Messages.CollectionChanged -= OnMessagesChanged;
            _chat.Compaction.PropertyChanged -= OnCompactionPropertyChanged;
        }
        _chat = (e.NewValue as IChatHost)?.Chat;
        if (_chat is not null)
        {
            _chat.Messages.CollectionChanged += OnMessagesChanged;
            _chat.Compaction.PropertyChanged += OnCompactionPropertyChanged;
        }
    }

    private void OnCompactionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CompactionPreview.Preview))
        {
            Dispatcher.InvokeAsync(() => CompactionScroll.ScrollToEnd());
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (MessageViewModel message in e.NewItems)
            {
                message.PropertyChanged += OnMessagePropertyChanged;
            }
        }
        if (e.OldItems is not null)
        {
            foreach (MessageViewModel message in e.OldItems)
            {
                message.PropertyChanged -= OnMessagePropertyChanged;
            }
        }
        ScrollToEnd();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e) => ScrollToEnd();

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0)
        {
            if (_stickToBottom)
            {
                MessagesScroll.ScrollToEnd();
            }
            return;
        }
        _stickToBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 8;
    }

    private bool _shelfMenuOpen;

    /// <summary>
    /// Кнопка 🗄: открыть/закрыть меню полок (Popup). Перед открытием перечитываем
    /// shelves.json — агент мог сам переключить полки, пока меню было закрыто.
    /// Popup сам закрывается на любой клик вне (StaysOpen=False) — включая клик по самой
    /// кнопке, — поэтому «было открыто» ведём отдельно: иначе повторный клик закроет и
    /// тут же снова откроет меню.
    /// </summary>
    private void ShelfButton_Click(object sender, RoutedEventArgs e)
    {
        _chat?.Shelves.Refresh();
        if (ShelfPopup is null)
        {
            return;
        }
        if (_shelfMenuOpen)
        {
            _shelfMenuOpen = false; // Popup уже закрылся на press
            return;
        }
        _shelfMenuOpen = true;
        ShelfPopup.IsOpen = true;
    }

    private void ShelfPopup_Closed(object sender, System.EventArgs e) => _shelfMenuOpen = false;

    /// <summary>Ctrl+V: если в буфере картинка — вкладываем её (текстовую вставку не трогаем).</summary>
    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || _chat is null)
        {
            return;
        }
        if (System.Windows.Clipboard.ContainsImage())
        {
            _chat.MessageCommands.PasteImageCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ScrollToEnd()
    {
        if (_stickToBottom)
        {
            Dispatcher.InvokeAsync(() => MessagesScroll.ScrollToEnd());
        }
    }
}
