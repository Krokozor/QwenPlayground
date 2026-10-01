using System.Text.Json;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.Memory;

/// <summary>
/// Хранилище долговременной памяти агента: один JSON-файл на факт в memories/
/// + компактный index.md (строка на память) для быстрого просмотра.
/// Масштаб — десятки/сотни фактов, поэтому без эмбеддингов: индекс читается напрямую.
///
/// Потоковая модель: все обращения в приложении идут с потока UI (heartbeat-таймер,
/// агентный цикл и сервисные вызовы сериализуются диспетчером), поэтому без локов.
/// Экземпляров может быть несколько (tool'ы создают свои) — это ок, состояние на диске.
/// Кросс-процессные записи (агент через shell) не защищаются: AtomicFile делает
/// публикацию атомарной, List переживает исчезновение файла на лету.
/// </summary>
public sealed class MemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;

    public MemoryStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(SelfBuildPaths.WorkspaceRoot, "memories");
        System.IO.Directory.CreateDirectory(_directory);
    }

    /// <summary>Каталог хранилища (memories/).</summary>
    public string Root => _directory;
    public string IndexFile => Path.Combine(_directory, "index.md");

    public MemoryItem Add(string content, string source = "agent")
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            // Пустой факт = невидимая карточка в UI и мусор в индексе — ловим у истока.
            throw new ArgumentException("memory content is empty", nameof(content));
        }
        var item = new MemoryItem
        {
            Content = content.Trim(),
            Source = source
        };
        Save(item);
        return item;
    }

    /// <summary>
    /// Перезаписывает файл факта (после классификации слоёв). Индекс пересобирается.
    /// Версионная проверка (оптимистичная конкурентность, без локов — модель однопоточная):
    /// файл должен существовать, и его CreatedAt должен совпадать с CreatedAt предмета.
    /// Иначе факт удалён/заменён ПОСЛЕ нашего снимка — классический сценарий: flush снял
    /// список, ушёл на сеть (await), агент за это время memory_merge'нул факт (Remove),
    /// flush возвращается и вслепую перезаписывает X.json — «воскресание» слитого факта
    /// (баг 2026-10-01, внешний код-ревью). Отказ — в events-лог, запись не выполняется.
    /// Легочный файл без CreatedAt (ticks == 0) версионных данных не несёт — запись допускается.
    /// </summary>
    public bool Update(MemoryItem item)
    {
        var file = Path.Combine(_directory, item.Id + ".json");
        if (!File.Exists(file))
        {
            NoteConflict(item, "файл не существует (факт удалён во время операции)");
            return false;
        }
        MemoryItem? disk;
        try
        {
            disk = JsonSerializer.Deserialize<MemoryItem>(File.ReadAllText(file));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            NoteConflict(item, $"файл повреждён: {exception.Message}");
            return false;
        }
        if (disk is null || (disk.CreatedAt.Ticks != 0 && disk.CreatedAt != item.CreatedAt))
        {
            NoteConflict(item, $"несовпадение версий (на диске: {disk?.CreatedAt:O}, в снимке: {item.CreatedAt:O})");
            return false;
        }
        Save(item);
        return true;
    }

    private static void NoteConflict(MemoryItem item, string reason)
    {
        AppEventLog.Log($"memory: Update({item.Id[..8]}) отклонён — {reason}; " +
                        "устаревший снимок не перезаписал актуальное состояние");
    }

    private void Save(MemoryItem item)
    {
        AtomicFile.WriteAllText(Path.Combine(_directory, item.Id + ".json"), JsonSerializer.Serialize(item, JsonOptions));
        RebuildIndex();
    }

    public bool Remove(string id)
    {
        var file = Path.Combine(_directory, id + ".json");
        if (!File.Exists(file))
        {
            return false;
        }
        File.Delete(file);
        RebuildIndex();
        return true;
    }

    public MemoryItem? Get(string id)
    {
        var file = Path.Combine(_directory, id + ".json");
        return File.Exists(file) ? JsonSerializer.Deserialize<MemoryItem>(File.ReadAllText(file)) : null;
    }

    public List<MemoryItem> List()
    {
        var items = new List<MemoryItem>();
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            // Имя файла = {id}.json: если совпадения нет (pairs.json, index-артефакты),
            // это не память — пропускаем. Иначе pairs.json десериализуется как MemoryItem
            // с пустым Content и случайным GUID (новый каждый раз) — «призрак» в UI.
            var fileName = Path.GetFileNameWithoutExtension(file);
            try
            {
                var item = JsonSerializer.Deserialize<MemoryItem>(File.ReadAllText(file));
                if (item is not null && item.Id == fileName)
                {
                    items.Add(item);
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // Повреждённый файл — пропускаем, как в SessionStore. IOException ловит и файл,
                // удалённый на лету (flush памяти/агент с memory_delete идут параллельно).
            }
        }
        return items.OrderByDescending(i => i.CreatedAt).ToList();
    }

    /// <summary>Пересобирает index.md: одна строка на память (id, категория, эмодзи, заголовок).</summary>
    public void RebuildIndex()
    {
        var lines = new List<string> { "# Индекс памяти", string.Empty };
        foreach (var item in List())
        {
            var title = item.Content.Length <= 80 ? item.Content : item.Content[..80] + ".";
            // Выводимые имена — из распределений классификатора; без слоёв помечаем.
            var category = MemoryClassifier.TopName(item.CategoryLayers);
            var emoji = MemoryClassifier.TopEmojiOf(item.EmojiLayers);
            var mark = item.HasSemanticLayers ? category : "без слоёв";
            lines.Add($"- [{item.Id}] ({mark}) {emoji}{title}");
        }
        AtomicFile.WriteAllText(IndexFile, string.Join('\n', lines) + '\n');
    }
}
