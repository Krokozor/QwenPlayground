using System.Windows;

namespace QwenPlayground.App.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string question)
    {
        InitializeComponent();
        QuestionText.Text = question;
    }

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnDeny(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
