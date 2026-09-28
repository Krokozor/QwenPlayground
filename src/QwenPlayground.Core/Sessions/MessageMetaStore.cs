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
        var dest = FreePath(dir, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, dest, overwrite: false);
        return dest;
    }

    /// <summary>
    /// Копирует файл в подпапку attachments/ (анонсируемые вложения: текст, pdf, что угодно
    /// немультимодальное). В отличие от AddArtifact (мультимодальное, прямо в msg_&lt;id&gt;/ и
    /// попадает в multimodal_data), эти файлы GetArtifacts НЕ видит (non-recursive) — их
    /// анонсирует тег &lt;attachment&gt; в сообщении, модель читает через read_file. Возвращает путь.
    /// </summary>
    public string AddFileArtifact(int msgId, string sourcePath)
    {
        var dir = Path.Combine(ArtifactsDir(msgId), "attachments");
        Directory.CreateDirectory(dir);
        var dest = FreePath(dir, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, dest, overwrite: false);
        return dest;
    }

    /// <summary>
    /// Свободное имя в папке: при одинаковых базовых именах (два скриншота shot.png, или
    /// shot.png + одноимённый документ) второе копирование не перетирало первое, а
    /// добавляло суффикс. Раньше был overwrite: true — два вложения молча схлопывались в
    /// одно: чипов в UI два, картинок у модели одна.
    /// </summary>
    private static string FreePath(string dir, string fileName)
    {
        var candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; i < 1000; i++)
        {
            var numbered = Path.Combine(dir, $"{stem} ({i}){extension}");
            if (!File.Exists(numbered))
            {
                return numbered;
            }
        }
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    /// <summary>
    /// Сохраняет текстовое содержимое в подпапку attachments/ папки артефактов сообщения
    /// (sessions/&lt;sid&gt;/artifacts/msg_&lt;id&gt;/attachments/&lt;fileName&gt;). Для анонсируемых
    /// (не мультимодальных) вложений: полный tool-вывод, документы. Возвращает путь к файлу.
    /// </summary>
    public string AddTextArtifact(int msgId, string fileName, string content)
    {
        var dir = Path.Combine(ArtifactsDir(msgId), "attachments");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, fileName);
        File.WriteAllText(dest, content);
        return dest;
    }
}
