using System.Windows;

namespace QwenPlayground.App.Views;

public partial class QuestionWindow : Window
{
    public string Answer { get; private set; } = string.Empty;

    public QuestionWindow(string question)
    {
        InitializeComponent();
        QuestionText.Text = question;
        AnswerBox.Focus();
    }

    private void OnAnswer(object sender, RoutedEventArgs e)
    {
        Answer = AnswerBox.Text;
        DialogResult = true;
        Close();
    }
}
