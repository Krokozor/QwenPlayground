using System.Text.Json;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// Пометки «рабочая папка → отслеживаемый C#-проект» (трекинг C#-тулов, 2026-11-04).
///
/// Пометка — персистентный ФАКТ (переживает перезапуск процесса), Roslyn-воркспейс —
/// per-process кэш: новая сессия в той же папке — трек на месте, воркспейс грузится
/// холодный. Единый источник правды и для тула csharp_track, и для UI (панель сессии).
///
/// Формат: JSON-карта root → projectFile (абсолютные пути). Ключ — рабочая папка
/// ТОЧНО (без гадания по предкам: пометка привязана к папке, а не к дереву).
/// </summary>
public sealed class CSharpProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly object _lock = new();
    private Dictionary<string, string> _marks;

    /// <summary>Общий экземпляр: config/csharp_projects.json в корне харнесса.</summary>
    public static CSharpProjectStore Shared { get; } =
        new(Path.Combine(SelfBuildPaths.WorkspaceRoot, "config", "csharp_projects.json"));

    public CSharpProjectStore(string filePath)
    {
        _filePath = filePath;
        _marks = Load();
    }

    /// <summary>Пометка для точного root (полный путь, регистронезависимо). null — нет.</summary>
    public string? Get(string root)
    {
        lock (_lock)
        {
            return _marks.TryGetValue(Normalize(root), out var file) ? file : null;
        }
    }

    public void Set(string root, string projectFile)
    {
        lock (_lock)
        {
            _marks[Normalize(root)] = Path.GetFullPath(projectFile);
            SaveLocked();
        }
    }

    /// <summary>true — пометка была и удалена; false — её не было.</summary>
    public bool Remove(string root)
    {
        lock (_lock)
        {
            var removed = _marks.Remove(Normalize(root));
            if (removed)
            {
                SaveLocked();
            }
            return removed;
        }
    }

    public IReadOnlyList<(string Root, string ProjectFile)> All()
    {
        lock (_lock)
        {
            return _marks
                .Select(kv => (Root: kv.Key, ProjectFile: kv.Value))
                .OrderBy(kv => kv.Root, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static string Normalize(string root)
    {
        var full = Path.GetFullPath(root);
        // GetFullPath хвостовой сепаратор СОВЕРШЕННО не убирает («V:\game\» ≠ «V:\game»),
        // хотя для папок это одно и то же. Корень диска («V:\») — не трогаем.
        if (full.Length > 3 && full[^1] is '\\' or '/')
        {
            full = full.AsSpan(0, full.Length - 1).ToString();
        }
        return full;
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath));
                if (json is not null)
                {
                    return new Dictionary<string, string>(json, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            // Повреждённый файл — стартуем с пустой карты; старый файл перезапишется
            // при первом Set. Трекинг не должен ронять приложение.
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }
            // Атомарная запись: temp + Replace (не оставляем полудохлый JSON при краше).
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(
                _marks.ToDictionary(kv => kv.Key, kv => kv.Value), JsonOptions));
            if (File.Exists(_filePath))
            {
                File.Replace(temp, _filePath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _filePath);
            }
        }
        catch
        {
            // Ошибка записи не роняет сессию: пометка живёт в памяти до перезапуска.
        }
    }
}
