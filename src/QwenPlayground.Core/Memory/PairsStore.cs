using System.IO;
using System.Text.Json;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.Memory;

/// <summary>
/// Пара-кандидат на слияние. Хранит полный вектор распределения по цифрам 0-9
/// (нормированный, сумма = 1) + схожесть гистограмм. Score/Entropy/Argmax —
/// вычисляемые свойства, не персистятся отдельно.
/// </summary>
public sealed record PendingPair(string A, string B, double HistOverlap, double[] DigitDist)
{
    /// <summary>Взвешенный балл схожести (Σ p(i)·i).</summary>
    public double Score => DigitDist is null ? 0 : DigitDist.Select((p, i) => p * i).Sum();

    /// <summary>Энтропия распределения по цифрам (биты, log2).</summary>
    public double Entropy => DigitDist is null ? 0 : DigitDist.Where(p => p > 0).Sum(p => -p * Math.Log2(p));

    /// <summary>Цифра с максимальной вероятностью (argmax).</summary>
    public int Argmax => DigitDist is null ? 0 : DigitDist.ToList().IndexOf(DigitDist.Max());

    /// <summary>Расстояние между двумя самыми высокими пиками (0 если модал).</summary>
    public int PeakGap
    {
        get
        {
            if (DigitDist is null || DigitDist.Length < 2) return 0;
            var sorted = DigitDist.Select((p, i) => (p, i)).OrderByDescending(x => x.p).Take(2).ToList();
            return Math.Abs(sorted[0].i - sorted[1].i);
        }
    }

    /// <summary>Отношение второго пика к первому (0..1). Близко к 1 = сильная бимодальность.</summary>
    public double SecondPeakRatio
    {
        get
        {
            if (DigitDist is null || DigitDist.Length < 2) return 0;
            var sorted = DigitDist.Select((p, i) => (p, i)).OrderByDescending(x => x.p).Take(2).ToList();
            return sorted[0].p > 0 ? sorted[1].p / sorted[0].p : 0;
        }
    }

    /// <summary>
    /// «Приоритет внимания»: насколько пара требует размышления агента.
    /// Максимален в серой зоне (score ~5) при высокой энтропии; минимален у краёв (очевидные вердикты).
    /// Attention = Entropy × (1 − |Score − 5| / 5)
    /// </summary>
    public double Attention => Entropy * Math.Max(0, 1 - Math.Abs(Score - 5) / 5.0);
}

/// <summary>
/// Связи пар воспоминаний, персистентные в memories/pairs.json:
///  - Distinct: пары, помеченные «это не похожие воспоминания» (false positive классификатора) —
///    больше НИКОГДА не поднимаются как кандидаты на слияние;
///  - Pending: пары-кандидаты, ожидающие разрешения основной моделью (копятся, не блокируют сканер),
///    каждая несёт факторы решения: схожесть гистограмм + балл/энтропия цифры классификатора.
///
/// Ключ пары упорядочен (id по возрастанию) — «A~B» и «B~A» это одна связь.
/// Файл перезаписывается атомарно при каждом изменении.
/// </summary>
public sealed class PairsStore
{
    private sealed class PairsFile
    {
        public List<string[]> Distinct { get; set; } = new();
        public List<PendingPair> Pending { get; set; } = new();
    }

    private readonly string _file;
    private readonly HashSet<string> _distinct = new(StringComparer.Ordinal);
    private readonly List<PendingPair> _pending = new();

    public PairsStore(string memoryRoot)
    {
        _file = Path.Combine(memoryRoot, "pairs.json");
        Load();
    }

    private static string Key(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

    /// <summary>Пара уже разведена как «не похожие»?</summary>
    public bool IsDistinct(string a, string b) => _distinct.Contains(Key(a, b));

    /// <summary>Кандидаты на слияние, ожидающие решения основной модели (первыми — старейшие).</summary>
    public IReadOnlyList<PendingPair> Pending => _pending;

    /// <summary>Все разведённые пары (для инспекции в UI и возврата в кандидаты).</summary>
    public IReadOnlyList<(string A, string B)> Distinct =>
        _distinct.Select(k => { var p = k.Split('|'); return (p[0], p[1]); }).ToList();

    /// <summary>Снять маркировку «не похожие» — пара снова может быть предложена сканером.</summary>
    public void UnmarkDistinct(string a, string b)
    {
        if (_distinct.Remove(Key(a, b)))
        {
            Save();
        }
    }

    /// <summary>Поставить пару в очередь на разрешение. Уже разведённые и дубликаты игнорируются.</summary>
    public void AddPending(string a, string b, double histOverlap = 0, double[]? digitDist = null)
    {
        if (a == b || IsDistinct(a, b))
        {
            return;
        }
        var key = Key(a, b);
        if (_pending.Any(p => Key(p.A, p.B) == key))
        {
            return;
        }
        _pending.Add(new PendingPair(a, b, histOverlap, digitDist ?? new double[10]));
        Save();
    }

    /// <summary>Модель развела пару: в Distinct, из очереди прочь.</summary>
    public void MarkDistinct(string a, string b)
    {
        _distinct.Add(Key(a, b));
        _pending.RemoveAll(p => Key(p.A, p.B) == Key(a, b));
        Save();
    }

    /// <summary>Сбросить всю очередь Pending (для перегенерации сканером).</summary>
    public void ClearPending()
    {
        if (_pending.Count > 0)
        {
            _pending.Clear();
            Save();
        }
    }

    /// <summary>Убрать пару из очереди без маркировки (вернуть в кандидаты сканера).</summary>
    public void RemovePending(string a, string b)
    {
        var changed = _pending.RemoveAll(p => Key(p.A, p.B) == Key(a, b)) > 0;
        if (changed)
        {
            Save();
        }
    }

    /// <summary>После merge/delete почистить пары, ссылающиеся на исчезнувшие id.</summary>
    public void Cleanup(IEnumerable<string> aliveIds)
    {
        var alive = aliveIds.ToHashSet(StringComparer.Ordinal);
        var changed = _pending.RemoveAll(p => !alive.Contains(p.A) || !alive.Contains(p.B)) > 0;
        changed |= _distinct.RemoveWhere(k =>
        {
            var ids = k.Split('|');
            return !alive.Contains(ids[0]) || !alive.Contains(ids[1]);
        }) > 0;
        if (changed)
        {
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_file))
        {
            return;
        }
        try
        {
            var file = JsonSerializer.Deserialize<PairsFile>(File.ReadAllText(_file));
            if (file is null)
            {
                return;
            }
            foreach (var pair in file.Distinct)
            {
                if (pair.Length == 2)
                {
                    _distinct.Add(Key(pair[0], pair[1]));
                }
            }
            foreach (var pair in file.Pending)
            {
                if (pair.A.Length > 0 && pair.B.Length > 0 && !IsDistinct(pair.A, pair.B))
                {
                    _pending.Add(pair);
                }
            }
        }
        catch (JsonException)
        {
            // битый файл — начинаем с пустых связей; классификатор накопит заново
        }
    }

    private void Save()
    {
        var file = new PairsFile
        {
            Distinct = _distinct.Select(k => k.Split('|')).ToList(),
            Pending = _pending.ToList()
        };
        AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
    }
}
