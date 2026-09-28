namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Прикреплённый к следующему сообщению файл (для мультимодальности). До отправки
/// это оригинал на диске — он может ещё писаться (снапшот-тул, adb pull, загрузка),
/// поэтому превью грузится с ожиданием стабилизации файла, а состояние неудачи
/// показывается честно (заглушка + «перезагрузить»), а не залипает и не рисует
/// чёрный прямоугольник.
/// </summary>
public sealed class PendingAttachment : AttachmentPreview
{
    public PendingAttachment(string name, string fullPath)
        : base(name, fullPath)
    {
    }
}
