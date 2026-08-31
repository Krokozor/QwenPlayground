using System.Windows;

namespace QwenPlayground.App.Views;

public partial class PromptWindow : Window
{
    public PromptWindow(string text)
    {
        InitializeComponent();
        DataContext = text;
    }
}
