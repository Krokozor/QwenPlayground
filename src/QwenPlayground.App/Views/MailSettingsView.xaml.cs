using System.Windows;
using System.Windows.Controls;
using QwenPlayground.Core.Mail;

namespace QwenPlayground.App.Views;

public partial class MailSettingsView : UserControl
{
    public MailSettingsView()
    {
        InitializeComponent();
        LoadSettings();
        // Wake policy combo: All/Folders/Senders.
        WakePolicyCombo.Items.Add("all");
        WakePolicyCombo.Items.Add("folders");
        WakePolicyCombo.Items.Add("senders");
    }

    private void LoadSettings()
    {
        var s = MailSettings.Get();
        EnabledCheck.IsChecked = s.Enabled;
        ImapHostBox.Text = s.ImapHost;
        ImapPortBox.Text = s.ImapPort.ToString();
        SmtpHostBox.Text = s.SmtpHost;
        SmtpPortBox.Text = s.SmtpPort.ToString();
        PollIntervalBox.Text = s.PollIntervalSeconds.ToString();
        WakePolicyCombo.SelectedIndex = s.WakePolicy switch
        {
            MailWakePolicy.All => 0,
            MailWakePolicy.Folders => 1,
            MailWakePolicy.Senders => 2,
            _ => 0,
        };
        WakeSendersBox.Text = string.Join(", ", s.WakeSenders);
        OAuthClientIdBox.Text = s.OAuthClientId;
        OAuthClientSecretBox.Password = s.OAuthClientSecret;
        EmailBox.Text = s.Email;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        MailSettings.Update(s =>
        {
            s.Enabled = EnabledCheck.IsChecked == true;
            s.ImapHost = ImapHostBox.Text.Trim();
            s.ImapPort = int.TryParse(ImapPortBox.Text, out var ip) ? ip : 993;
            s.SmtpHost = SmtpHostBox.Text.Trim();
            s.SmtpPort = int.TryParse(SmtpPortBox.Text, out var sp) ? sp : 465;
            s.PollIntervalSeconds = int.TryParse(PollIntervalBox.Text, out var pi) ? Math.Max(10, pi) : 60;
            s.WakePolicy = WakePolicyCombo.SelectedIndex switch
            {
                1 => MailWakePolicy.Folders,
                2 => MailWakePolicy.Senders,
                _ => MailWakePolicy.All,
            };
            var senders = WakeSendersBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => x.Length > 0)
                .ToList();
            s.WakeSenders = senders;
            s.OAuthClientId = OAuthClientIdBox.Text.Trim();
            if (OAuthClientSecretBox.Password.Length > 0)
            {
                s.OAuthClientSecret = OAuthClientSecretBox.Password;
            }
        });
        if (MailSettings.Configured)
        {
            MailMonitor.TryStart();
        }
        else
        {
            MailMonitor.Stop();
        }
        LoadSettings(); // перерисовать из сохранённых (normalize)
        var btn = (Button)sender;
        btn.Content = "✓ Сохранено";
        btn.IsEnabled = false;
        // Вернуть надпись через 1.5 с.
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(1500)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            btn.Content = "Сохранить";
            btn.IsEnabled = true;
        };
        timer.Start();
    }
}
