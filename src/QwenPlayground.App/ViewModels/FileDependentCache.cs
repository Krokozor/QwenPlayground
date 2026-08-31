using System.IO;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Кэш значения, пересчитываемого только при изменении файлов-зависимостей
/// (mtime/существование). Проверка mtime — чтение метаданных, на порядки дешевле
/// полного перечитывания и парсинга. Для хот-пути рендера: системный промпт
/// (main-agent.md + layers.json + trajectory.md) собирается на каждой итерации хода,
/// журнал сборок читается при каждом state-блоке.
/// Потокобезопасность не нужна: потребители живут на потоке UI.
/// </summary>
public sealed class FileDependentCache<T>
{
    private readonly string[] _paths;
    private readonly Func<T> _build;
    private (DateTime MtimeUtc, bool Exists)[]? _snapshot;
    private T _value;

    public FileDependentCache(IEnumerable<string> paths, Func<T> build, T initial)
    {
        _paths = paths.ToArray();
        _build = build;
        _value = initial;
    }

    public T Get()
    {
        // Снапшот считается ВСЕГДА (иначе первая сборка записала бы пустой снапшот и кэш
        // перевычислялся бы на каждом вызове — тесты FileDependentCacheTests ловят именно это).
        var snapshot = new (DateTime MtimeUtc, bool Exists)[_paths.Length];
        var changed = _snapshot is null;
        for (var i = 0; i < _paths.Length; i++)
        {
            // Существование проверяется отдельно: «файла не было → появился» тоже инвалидация.
            var info = new FileInfo(_paths[i]);
            snapshot[i] = info.Exists
                ? (info.LastWriteTimeUtc, true)
                : (DateTime.MinValue, false);
            if (!changed && _snapshot is not null &&
                (snapshot[i].Exists != _snapshot[i].Exists || snapshot[i].MtimeUtc != _snapshot[i].MtimeUtc))
            {
                changed = true;
            }
        }
        if (changed)
        {
            _value = _build();
            _snapshot = snapshot;
        }
        return _value;
    }
}
