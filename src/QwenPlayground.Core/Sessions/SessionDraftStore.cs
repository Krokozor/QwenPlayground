using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.Sessions;

/// <summary>
/// Драфт окошка ввода сессии: sessions/&lt;id&gt;/draft.txt — маленький ОТДЕЛЬНЫЙ файл
/// (не в chat.json). Зачем отдельно: (а) размер файла сессии не раздувается черновиком,
/// (б) легко знать, есть ли у сессии несохранённый драфт (Exists), (в) формат chat.json
/// не трогаем — обратная совместимость чатов целая.
///
/// Запись атомарная (AtomicFile): обрыв питания посреди записи не оставляет битого драфта.
/// Пустой текст = отсутствие драфта (Clear), чтобы не плодить пустые файлики.
/// </summary>
public sealed class SessionDraftStore
{
    private const string DraftFile = "draft";

    private readonly string _directory;

    public SessionDraftStore(string directory)
    {
        _directory = directory;
    }

    private string DraftPath(string id) => Path.Combine(_directory, id, DraftFile + ".txt");

    /// <summary>Сохранить драфт сессии (атомарно). Пустой текст — то же, что <see cref="Clear"/>.</summary>
    public void Save(string id, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            Clear(id);
            return;
        }
        var path = DraftPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, text);
    }

    /// <summary>Прочитать драфт сессии. null — драфта нет (файла нет или он пуст).</summary>
    public string? Load(string id)
    {
        var path = DraftPath(id);
        if (!File.Exists(path))
        {
            return null;
        }
        var text = File.ReadAllText(path);
        return text.Length == 0 ? null : text;
    }

    /// <summary>Удалить драфт сессии (если есть). Текст уже отправлен или очищен.</summary>
    public void Clear(string id)
    {
        var path = DraftPath(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Есть ли у сессии драфт.</summary>
    public bool Exists(string id) => File.Exists(DraftPath(id));
}
