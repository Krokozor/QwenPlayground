using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QwenPlayground.App.ViewModels;

namespace QwenPlayground.App.Views;

public partial class ChatView : UserControl
{
    private MainViewModel? _viewModel;
    private bool _stickToBottom = true;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        MessagesScroll.ScrollChanged += OnScrollChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Messages.CollectionChanged -= OnMessagesChanged;
            _viewModel.Compaction.PropertyChanged -= OnCompactionPropertyChanged;
        }
        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Messages.CollectionChanged += OnMessagesChanged;
            _viewModel.Compaction.PropertyChanged += OnCompactionPropertyChanged;
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

    /// <summary>Ctrl+V: если в буфере картинка — вкладываем её (текстовую вставку не трогаем).</summary>
    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || _viewModel is null)
        {
            return;
        }
        if (System.Windows.Clipboard.ContainsImage())
        {
            _viewModel.PasteImageCommand.Execute(null);
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
