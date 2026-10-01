using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QwenPlayground.App.Desktop;
using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Compaction;

namespace QwenPlayground.App.Views;

public partial class ChatView : UserControl
{
    // Хост (MainViewModel — главное окно, ChatWindowViewModel — отдельное окно чата):
    // резолвим Chat через IChatHost, не привязываясь к конкретному DataContext.
    private ChatViewModel? _chat;

    // ── «Залипание» к концу чата ────────────────────────────────────────────────────
    // Логика: пользователь на самом конце → новые сообщения (и рост стрима) тянут его
    // за собой; уехал читать историю → не трогаем.
    //
    // Флаг _stickToBottom обновляется ТОЛЬКО реальным скроллом пользователя (колесо,
    // бар, клавиши). Программные ScrollToEnd помечаются заранее (_programmaticOffset —
    // позиция, которую они дадут) и их ScrollChanged-события распознаются и пропускаются:
    // иначе наш же скролл перезаписывал бы флаг «внизу=true» и тянул обратно.
    //
    // Старый код (флаг из ScrollChanged + отложенный Dispatcher.InvokeAsync) ломался
    // гонкой: скролл, поставленный в очередь ДО того, как пользователь уехал вверх,
    // выполнялся ПОСЛЕ (Normal-приоритет — раньше layout) и тянул его обратно в конец,
    // а его же ScrollChanged снова вешал флаг. Плюс combined-события (extent+offset
    // за один layout) проходили через `return` и флаг не обновляли.
    private const double StickTolerance = 8;
    private bool _stickToBottom = true;
    private double _programmaticOffset = double.NaN; // позиция нашего последнего ScrollToEnd
    private double _lastOffset; // .NET 10: у ScrollChangedEventArgs больше нет VerticalOffsetChange —
                                // двигаемость offset считаем сами, по предыдущему значению

    /// <summary>
    /// Встроенный режим (чат субагента в пузыре tool call): без тулбара сессий и без
    /// усилие/вложений — сообщения + ввод + стоп. Ставится в XAML (Embedded="True").
    /// </summary>
    public bool Embedded { get; set; }

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
        // Встроенный режим: DataContext — сам ChatViewModel (пузырь tool call),
        // в окнах — хост (IChatHost) с свойством Chat.
        _chat = (e.NewValue as IChatHost)?.Chat ?? e.NewValue as ChatViewModel;
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
        // Полная пересборка (Clear + Add: смена сессии/загрузка истории/откат) —
        // открываем с конца, независимо от того, где пользователь был.
        bool fullRebuild = e.Action == NotifyCollectionChangedAction.Reset;
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
        ScrollToEnd(force: fullRebuild);
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e) => ScrollToEnd();

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        bool offsetMoved = Math.Abs(e.VerticalOffset - _lastOffset) > 0.001;
        _lastOffset = e.VerticalOffset;

        // 1) Наш собственный программный скролл (DoScrollToEnd заранее записал целевую
        // позицию) — пропускаем, чтобы не перезаписывать флаг «внизу» своим же скроллом.
        if (offsetMoved && !double.IsNaN(_programmaticOffset) &&
            Math.Abs(e.VerticalOffset - _programmaticOffset) < 0.5)
        {
            _programmaticOffset = double.NaN;
            return;
        }

        // 2) Позиция сместилась — это скролл ПОЛЬЗОВАТЕЛЯ (колесо/бар/клавиши), и он
        // ПРОВЕРЯЕТСЯ ПЕРВЫМ: при активном стриме в том же событии меняется и extent
        // (контент растёт), и если рост контента имеет приоритет, скролл пользователя
        // проглатывается и вид тянет обратно в конец (оригинальный баг). Прилипаем,
        // только если пользователь в самом конце; сам долистал до конца — залипание
        // вернулось.
        if (offsetMoved)
        {
            _stickToBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - StickTolerance;
            return;
        }

        // 3) Позиция не менялась: рост контента (новое сообщение/стрим) или resize окна.
        // Если прилипли — остаёмся внизу (низ уехал вместе с extent/viewport).
        if (_stickToBottom)
        {
            DoScrollToEnd();
        }
    }

    /// <summary>
    /// Программный скролл в конец: сначала записываем позицию, которую он даст
    /// (максимальный offset, clamp'нутый в 0..extent-viewport), — ScrollChanged
    /// распознает наш скролл по совпадению и не тронет флаг залипания.
    /// </summary>
    private void DoScrollToEnd()
    {
        _programmaticOffset = Math.Max(0, MessagesScroll.ExtentHeight - MessagesScroll.ViewportHeight);
        MessagesScroll.ScrollToEnd();
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

    // Рабочая папка сессии (план 2026-10-01): Enter в поле — применить (валидация в VM).
    private void SessionRootBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ChatViewModel chat && chat.SessionList is { } list)
        {
            list.ApplySessionRootCommand.Execute(null);
            e.Handled = true;
        }
    }

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

    /// <summary>
    /// Скролл к концу, если прилипли (или force: отправка/пересборка/смена сессии —
    /// прилипаем независимо от позиции). Синхронно (UI-поток): новый контент ещё не
    /// в layout, так что ScrollToEnd может быть no-op на старом extent — доработает
    /// событие ExtentHeightChange после layout (ветка 1 в OnScrollChanged).
    /// </summary>
    private void ScrollToEnd(bool force = false)
    {
        if (force)
        {
            _stickToBottom = true;
        }
        if (!_stickToBottom)
        {
            return;
        }
        DoScrollToEnd();
    }
}
