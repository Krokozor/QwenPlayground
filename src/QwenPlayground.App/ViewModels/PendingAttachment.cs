using System.Windows.Media;

namespace QwenPlayground.App.ViewModels;

/// <summary>Прикреплённый к следующему сообщению файл (для мультимодальности).</summary>
public sealed record PendingAttachment(string Name, string FullPath)
{
    private ImageSource? _preview;

    public bool IsImage => ChatPreview.IsImageFile(FullPath);

    /// <summary>Декодируется один раз: WPF-биндинг дёргает геттер на каждом layout-проходе.</summary>
    public ImageSource? Preview => IsImage ? (_preview ??= ChatPreview.Load(FullPath)) : null;
}
