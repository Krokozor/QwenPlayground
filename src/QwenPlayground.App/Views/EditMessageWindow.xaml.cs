using System.Windows;
using QwenPlayground.App.ViewModels;

namespace QwenPlayground.App.Views;

public partial class EditMessageWindow : Window
{
    private readonly MessageViewModel _message;
    private readonly Action _onChanged;
    private readonly string _oldReasoning;
    private readonly string _oldContent;
    private readonly bool _oldThinkingClosed;

    public EditMessageWindow(MessageViewModel message, Action onChanged)
    {
        InitializeComponent();
        _message = message;
        _onChanged = onChanged;
        _oldReasoning = message.Reasoning;
        _oldContent = message.Content;
        _oldThinkingClosed = message.ThinkingClosed;

        ReasoningBox.Text = message.Reasoning;
        ContentBox.Text = message.Content;
        ThinkingClosedBox.IsChecked = message.ThinkingClosed;

        if (message.Role != "assistant")
        {
            ReasoningHeader.Visibility = Visibility.Collapsed;
            ReasoningBox.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _message.Reasoning = ReasoningBox.Text;
        _message.Content = ContentBox.Text;
        _message.ThinkingClosed = ThinkingClosedBox.IsChecked == true;
        if (_message.Source is { } source)
        {
            source.Reasoning = _message.Reasoning.Length > 0 ? _message.Reasoning : null;
            source.Content = _message.Content;
            source.ThinkingClosed = _message.ThinkingClosed;
        }
        _onChanged();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _message.Reasoning = _oldReasoning;
        _message.Content = _oldContent;
        _message.ThinkingClosed = _oldThinkingClosed;
        Close();
    }
}
