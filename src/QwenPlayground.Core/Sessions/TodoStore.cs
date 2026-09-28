using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.Sessions;

/// <summary>
/// Пункт TODO-списка сессии. Text — цель (одна строка), Done — отмечен выполненным.
/// </summary>
public sealed class TodoItem
{
    public string Text { get; set; } = string.Empty;
    public bool Done { get; set; }
}

/// <summary>
/// TODO-список сессии: файл TODO.json в каталоге сессии (sessions/&lt;id&gt;/TODO.json).
/// Пер-сессия: каждая сессия (main, окна, субагенты) ведёт свой список — как shelves.json.
/// Если файла нет — списка нет (не создаём пустой).
///
/// LastModifiedBy — кто изменил список в последний раз: "agent" (тулы TODO_add/TODO_manage)
/// или "user" (ручная правка в UI). Напоминание в state-блоке подписывает источник, если
/// это пользователь, — чтобы агент не гадал, почему список целей поменялся.
/// </summary>
public sealed class TodoList
{
    public int Version { get; set; } = 1;
    public List<TodoItem> Items { get; set; } = new();
    public string LastModifiedBy { get; set; } = TodoStore.SourceAgent;
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Файловое хранилище TODO-списка сессии (TODO.json). Чтение: нет файла → null (список
/// отсутствует). Запись — атомарная (AtomicFile) и ставит штамп источника/времени.
/// Экземпляры создаются ad-hoc (тул агента, UI, state-напоминатель) — для живого
/// обновления UI есть статическое событие <see cref="Saved"/>.
/// </summary>
public sealed class TodoStore
{
    public const string FileName = "TODO.json";
    public const string SourceAgent = "agent";
    public const string SourceUser = "user";

    /// <summary>
    /// TODO.json сохранён (каталог сессии). Статическое событие: экземпляры создаются
    /// ad-hoc в разных местах (тул, UI), подписчики фильтруют по каталогу. Срабатывает
    /// на каждом Save (включая пустые изменения) — подписчик сам решает, перерисовывать ли.
    /// </summary>
    public static event Action<string>? Saved;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _sessionDir;

    public TodoStore(string sessionDir)
    {
        _sessionDir = sessionDir;
    }

    public string FilePath => Path.Combine(_sessionDir, FileName);

    /// <summary>Читает список; null — файла нет (списка нет).</summary>
    public TodoList? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }
            var list = JsonSerializer.Deserialize<TodoList>(File.ReadAllText(FilePath), JsonOptions);
            if (list is null)
            {
                return null;
            }
            list.Items ??= new List<TodoItem>();
            list.Items = list.Items.Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Text)).ToList();
            return list;
        }
        catch (JsonException)
        {
            // Битый файл не роняет агента: ведём себя так, будто списка нет;
            // следующий Save перезапишет его исправным.
            return null;
        }
    }

    /// <summary>Читает список или создаёт пустой (без записи на диск — пустой не пишем).</summary>
    public TodoList LoadOrCreate() => Load() ?? new TodoList();

    /// <summary>
    /// Атомарно сохраняет список, ставит штамп источника/времени и поднимает <see cref="Saved"/>.
    /// Пустой список (0 пунктов) УДАЛЯЕТ файл: «нет файла — нет списка».
    /// </summary>
    public void Save(TodoList list, string source)
    {
        list.LastModifiedBy = string.IsNullOrWhiteSpace(source) ? SourceAgent : source;
        list.UpdatedAt = DateTime.Now;
        list.Items = (list.Items ?? new List<TodoItem>())
            .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Text))
            .ToList();
        if (list.Items.Count == 0)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
            catch
            {
                // Не удалось удалить — не критично (файл перезапишется при следующем Save).
            }
        }
        else
        {
            Directory.CreateDirectory(_sessionDir);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(list, JsonOptions));
        }
        Saved?.Invoke(_sessionDir);
    }

    /// <summary>
    /// Контентный отпечаток списка (тексты + статусы done, без метаданных): для детекта
    /// «список изменился со последнего напоминания». Порядок пунктов учитывается.
    /// </summary>
    public static string ContentHash(TodoList list)
    {
        var payload = new StringBuilder();
        foreach (var item in list.Items)
        {
            payload.Append(item.Text).Append('\u0001').Append(item.Done ? 1 : 0).Append('\u0002');
        }
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(payload.ToString()));
        return Convert.ToHexString(bytes)[..16];
    }
}
