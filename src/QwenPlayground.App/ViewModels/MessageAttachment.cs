namespace QwenPlayground.App.ViewModels;

/// <summary>Вложение сообщения: копия файла в <c>artifacts/msg_&lt;id&gt;/</c>.</summary>
public sealed class MessageAttachment : AttachmentPreview
{
    public MessageAttachment(string name, string fullPath)
        : base(name, fullPath)
    {
    }

    /// <summary>Логическая высота превью в пузыре; на неё же завязан XAML (x:Static).</summary>
    public const double MessageAttachmentPreviewHeight = 140;
}
