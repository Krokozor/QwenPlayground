using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.Core.Chat;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Команды операций над сообщениями чата: откат, просмотр промпта, редактирование,
/// копирование, реколл/продолжение, вложения. Коллекции (Messages, PendingAttachments)
/// и состояние чата живут в MainViewModel — сюда приходят ссылки и тонкие делегаты
/// (canInteract, isGenerating, generate, saveCurrent, refreshPreview, status); доменная
/// логика — в Core (ChatLog, TurnPipeline).
/// </summary>
public sealed partial class MessageCommandsViewModel : ObservableObject {
    private readonly ChatLog _log;
    private readonly Func<bool> _canInteract;
    private readonly Func<bool> _isGenerating;
    private readonly Func<bool, Task> _generate;
    private readonly Action _saveCurrent;
    private readonly Action _refreshPreview;
    private readonly Action<string> _status;

    public ObservableCollection<MessageViewModel> Messages { get; }
    public ObservableCollection<PendingAttachment> PendingAttachments { get; }

    public MessageCommandsViewModel(
        ObservableCollection<MessageViewModel> messages,
        ObservableCollection<PendingAttachment> pendingAttachments,
        ChatLog log,
        Func<bool> canInteract,
        Func<bool> isGenerating,
        Func<bool, Task> generate,
        Action saveCurrent,
        Action refreshPreview,
        Action<string> status) {
        Messages = messages;
        PendingAttachments = pendingAttachments;
        _log = log;
        _canInteract = canInteract;
        _isGenerating = isGenerating;
        _generate = generate;
        _saveCurrent = saveCurrent;
        _refreshPreview = refreshPreview;
        _status = status;
    }

    private bool CanInteract() => _canInteract();

    /// <summary>
    /// Переоценить CanExecute всех команд: вызывается VM при смене IsGenerating
    /// (Reroll/Continue зависят от него). Остальные (CanInteract) WPF сам ре-кверит
    /// через CommandManager на активности пользователя.
    /// </summary>
    public void NotifyCanExecuteChanged() {
        RollbackCommand.NotifyCanExecuteChanged();
        InspectPromptCommand.NotifyCanExecuteChanged();
        EditMessageCommand.NotifyCanExecuteChanged();
        CopyMessageCommand.NotifyCanExecuteChanged();
        CopyChatCommand.NotifyCanExecuteChanged();
        RerollCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
        AttachFilesCommand.NotifyCanExecuteChanged();
        RemoveAttachmentCommand.NotifyCanExecuteChanged();
        PasteImageCommand.NotifyCanExecuteChanged();
        OpenAttachmentCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void Rollback(MessageViewModel? message) {
        if (message is null)
            return;

        var index = Messages.IndexOf(message);

        if (index < 0)
            return;

        while (Messages.Count > index) {
            Messages.RemoveAt(Messages.Count - 1);
        }

        _log.RemoveFrom(index);
        _saveCurrent();
    }

    [RelayCommand]
    private void InspectPrompt(MessageViewModel? message) {
        var text = message?.GetInspectionText() ?? "(нет данных генерации)";
        new Views.PromptWindow(text) { Owner = System.Windows.Application.Current.MainWindow }.Show();
    }

    [RelayCommand]
    private void EditMessage(MessageViewModel? message) {
        if (_isGenerating() || message?.Source is null)
            return;

        new Views.EditMessageWindow(message, OnMessageEdited) {
            Owner = System.Windows.Application.Current.MainWindow
        }.ShowDialog();
    }

    private void OnMessageEdited() {
        _refreshPreview();
        _saveCurrent();
    }

    [RelayCommand]
    private void CopyMessage(MessageViewModel? message) {
        if (message is null) return;
        var sb = new StringBuilder();
        if (message.Reasoning.Length > 0) {
            sb.Append("[мысли]\n").Append(message.Reasoning).Append('\n');
        }
        foreach (var tc in message.ToolCalls) {
            sb.Append(tc).Append('\n');
        }
        sb.Append(message.Content);
        System.Windows.Clipboard.SetText(sb.ToString());
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void CopyChat() {
        var builder = new StringBuilder();
        foreach (var message in Messages) {
            builder.Append("### ").Append(message.Role).Append('\n');
            if (message.Reasoning.Length > 0) {
                builder.Append("[reasoning]\n").Append(message.Reasoning).Append('\n');
            }
            if (message.Content.Length > 0) {
                builder.Append(message.Content).Append('\n');
            }
            foreach (var call in message.ToolCalls) {
                builder.Append("[tool call] ").Append(call).Append('\n');
            }
            builder.Append('\n');
        }
        System.Windows.Clipboard.SetText(builder.ToString());
        _status("чат скопирован в буфер обмена");
    }

    [RelayCommand(CanExecute = nameof(CanReroll))]
    private async Task RerollAsync(MessageViewModel? message) {
        if (message is null)
            return;

        Rollback(message);
        await _generate(false);
        _saveCurrent();
    }

    private bool CanReroll(MessageViewModel? message) =>
        !_isGenerating() && message is not null && Messages.Count > 0 &&
        ReferenceEquals(message, Messages[^1]) && message.Role == "assistant";

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task ContinueAsync() {
        await _generate(true);
        _saveCurrent();
    }

    private bool CanContinue() =>
        !_isGenerating() && Messages.Count > 0 && Messages[^1].Role == "assistant";

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void AttachFiles() {
        var dialog = new Microsoft.Win32.OpenFileDialog {
            Multiselect = true,
            Title = "Прикрепить файлы"
        };
        if (dialog.ShowDialog() != true)
            return;
        // Все файлы — во вложения. Картинки уходят мультимодально (маркер + base64 в рендере),
        // остальные (txt, pdf, ...) — как анонсируемые аттачменты (attachments/ + тег
        // <attachment> в сообщении), я читаю их через read_file. Текст в ввод больше не
        // вставляется: не раздувает сообщение и не обрезает крупные файлы.
        foreach (var file in dialog.FileNames) {
            PendingAttachments.Add(new PendingAttachment(Path.GetFileName(file), file));
        }
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void RemoveAttachment(PendingAttachment? attachment) {
        if (attachment is not null) {
            PendingAttachments.Remove(attachment);
        }
    }

    /// <summary>Вставить картинку из буфера обмена во вложения (без текста).</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void PasteImage() {
        if (!System.Windows.Clipboard.ContainsImage()) {
            _status("в буфере обмена нет картинки");
            return;
        }
        try {
            var image = System.Windows.Clipboard.GetImage();
            if (image is null)
                return;

            var dir = Path.Combine(Path.GetTempPath(), "qwen-paste");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"paste-{DateTime.Now:yyyyMMdd-HHmmssfff}.png");
            using (var stream = File.Create(file)) {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                encoder.Save(stream);
            }
            PendingAttachments.Add(new PendingAttachment(Path.GetFileName(file), file));
            _status("картинка из буфера добавлена во вложения");
        }
        catch {
            _status("не удалось вставить картинку из буфера");
        }
    }

    /// <summary>Открыть прикреплённый файл системным просмотрщиком.</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void OpenAttachment(MessageAttachment? attachment) {
        if (attachment is null || !File.Exists(attachment.FullPath))
            return;

        try {
            Process.Start(new ProcessStartInfo(attachment.FullPath) { UseShellExecute = true });
        }
        catch {
            // просмотрщик не открылся — профилактика
        }
    }
}
