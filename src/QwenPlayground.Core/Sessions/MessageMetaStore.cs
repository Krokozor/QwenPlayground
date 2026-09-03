using System.IO;

namespace QwenPlayground.Core.Sessions;

/// <summary>
/// Хранилище артефактов сообщений: файловая система как Side-Store. Каждое сообщение
/// имеет папку &lt;sessionDir&gt;/artifacts/msg_&lt;id&gt;/, куда копируются прикреплённые
/// файлы (картинки для мультимодальности, документы и т.п.). Адресация — (msgId, имя файла);
/// список вложений = список файлов в папке. Cleanup на компакции = удалить папку сообщения.
/// Копируем файл (не держим внешнюю ссылку) — «путь есть, файла нет» исключено.
/// </summary>
public sealed class MessageMetaStore
{
    private readonly string _artifactsRoot;

    public MessageMetaStore(string sessionDirectory)
    {
        _artifactsRoot = Path.Combine(sessionDirectory, "artifacts");
    }

    /// <summary>Папка артефактов сообщения (msg_&lt;id&gt;).</summary>
    public string ArtifactsDir(int msgId) => Path.Combine(_artifactsRoot, "msg_" + msgId);

    /// <summary>
    /// Удаляет все артефакты сообщения (папку msg_&lt;id&gt;). Возвращает число удалённых файлов.
    /// Освобождает контекст: маркеры вложений исчезают из рендера, base64 не отправляется.
    /// </summary>
    public int RemoveArtifacts(int msgId)
    {
        var dir = ArtifactsDir(msgId);
        if (!Directory.Exists(dir))
        {
            return 0;
        }
        var count = Directory.GetFiles(dir).Length;
        Directory.Delete(dir, recursive: true);
        return count;
    }

    /// <summary>Список полных путей к файлам сообщения (пусто, если папки нет).</summary>
    public IReadOnlyList<string> GetArtifacts(int msgId)
    {
        var dir = ArtifactsDir(msgId);
        return Directory.Exists(dir) ? Directory.GetFiles(dir) : Array.Empty<string>();
    }

    /// <summary>Копирует файл в папку артефактов сообщения. Возвращает путь к копии.</summary>
    public string AddArtifact(int msgId, string sourcePath)
    {
        var dir = ArtifactsDir(msgId);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, dest, overwrite: true);
        return dest;
    }
}
