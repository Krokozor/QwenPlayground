using QwenPlayground.Core.Probes;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Memory;

/// <summary>
/// Поиск похожих воспоминаний силами компаньон-модели (режим надмоза, включается,
/// когда все факты уже классифицированы). Пара оценивается одним токеном-цифрой 0–9;
/// распределение по цифрам берём из top_logprobs пробы — уверенность (энтропия по цифрам)
/// достаётся бесплатно, второй сэмпл не нужен.
///
/// Лестница вердиктов: 6–9 при низкой энтропии → Similar; 0–3 при низкой → Distinct;
/// середина или высокая энтропия → Uncertain (эскалация к основной модели).
/// Кандидаты для референса сортируются той же метрикой гистограмм, что и реколл
/// (<see cref="MemoryRecall.ScoreSemantic"/>); разведённые пары (PairsStore.Distinct) исключаются.
/// </summary>
public static class MemorySimilarity
{
    public enum Verdict { Similar, Distinct, Uncertain }

    public sealed record PairJudgement(Verdict Kind, double Score, double Entropy, double[] Distribution);

    /// <summary>Промпт пары: один токен-цифра. Простая шкала + явное «один проект ≠ один факт».</summary>
    public static string BuildPairPrompt(string contentA, string contentB)
    {
        return
            "Rate how similar these two memories are as FACTS.\n" +
            "0-3 = different facts (same project or topic does NOT make them similar)\n" +
            "4-5 = related facts about the same area, but each has unique information\n" +
            "6-9 = the same fact, restated or with only minor detail differences\n" +
            "Answer with ONE digit only.\n\n" +
            "[A]\n" + contentA + "\n\n[B]\n" + contentB;
    }

    /// <summary>
    /// Вердикт из первой позиции пробы: собираем распределение по цифрам 0–9 из top-токенов
    /// (токены вроде «9»/« 9»), нормируем, считаем взвешенный балл и энтропию по цифрам.
    /// Цифр в топе нет / пусто → Uncertain с максимальной энтропией.
    /// Пороги по умолчанию из AppSettings; для тестов можно передать явные.
    /// </summary>
    public static PairJudgement Judge(
        ProbeResult firstPosition,
        double? similarMin = null,
        double? distinctMax = null,
        double? confidentMaxEntropy = null)
    {
        var similarMinScore = similarMin ?? AppSettings.Get().SimilaritySimilarMin;
        var distinctMaxScore = distinctMax ?? AppSettings.Get().SimilarityDistinctMax;
        var confidentMax = confidentMaxEntropy ?? AppSettings.Get().SimilarityConfidentMaxEntropy;

        var digits = new double[10];
        var total = 0.0;
        foreach (var token in firstPosition.TopTokens)
        {
            var digit = DigitOf(token.Token);
            if (digit is null)
            {
                continue;
            }
            var p = Math.Exp(token.LogProb);
            digits[digit.Value] += p;
            total += p;
        }
        if (total <= 0)
        {
            return new PairJudgement(Verdict.Uncertain, 4.5, Math.Log2(10), new double[10]);
        }

        var dist = new double[10];
        var score = 0.0;
        var entropy = 0.0;
        for (var d = 0; d < 10; d++)
        {
            var p = digits[d] / total;
            dist[d] = p;
            score += p * d;
            if (p > 0)
            {
                entropy -= p * Math.Log2(p);
            }
        }

        var kind =
            entropy > confidentMax ? Verdict.Uncertain :
            score >= similarMinScore ? Verdict.Similar :
            score <= distinctMaxScore ? Verdict.Distinct :
            Verdict.Uncertain;
        return new PairJudgement(kind, score, entropy, dist);
    }

    private static int? DigitOf(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }
        // Лlama-токенизаторы режут по-разному: «9», « 9» — берём первую встреченную цифру.
        foreach (var ch in token)
        {
            if (ch is >= '0' and <= '9')
            {
                return ch - '0';
            }
        }
        return null;
    }

    /// <summary>Симметричная схожесть гистограмм двух фактов (усреднение двух направлений нормировки).</summary>
    public static double PairOverlap(MemoryItem a, MemoryItem b)
    {
        var ab = MemoryRecall.ScoreSemantic(
            new MemorySemanticLayers { Categories = a.CategoryLayers, Emoji = a.EmojiLayers },
            b.CategoryLayers, b.EmojiLayers);
        var ba = MemoryRecall.ScoreSemantic(
            new MemorySemanticLayers { Categories = b.CategoryLayers, Emoji = b.EmojiLayers },
            a.CategoryLayers, a.EmojiLayers);
        return (ab + ba) / 2;
    }

    /// <summary>
    /// Кандидаты под референс: остальные факты, отсортированные по схожести гистограмм,
    /// без разведённых пар (PairsStore) и без самого референса.
    /// Вызывать только когда все факты имеют слои — иначе сравнивать нечем.
    /// </summary>
    public static IReadOnlyList<MemoryItem> CandidatesFor(
        MemoryItem reference, IReadOnlyList<MemoryItem> others, PairsStore pairs)
    {
        return others
            .Where(o => o.Id != reference.Id && !pairs.IsDistinct(reference.Id, o.Id))
            .OrderByDescending(o => PairOverlap(reference, o))
            .ToList();
    }

    public sealed record ScanPassReport(int Probes, int QueuedSimilar, int MarkedDistinct);

    /// <summary>
    /// Один проход сканера дубликатов (вызывается на heartbeat, когда классификация догнала):
    /// идём по референсам от старейшего, для каждого — кандидаты по убыванию схожести гистограмм;
    /// пары, уже ждущие решения или разведённые, пробами не тратим. Бюджет проб за проход —
    /// чтобы компаньон не пахал вечно. Вердикт Distinct закрывается сразу (необратимо ничего),
    /// Similar/Uncertain копятся в Pending — разрешает основная модель.
    /// </summary>
    public static async Task<ScanPassReport> ScanPassAsync(
        MemoryStore store,
        PairsStore pairs,
        string endpoint,
        int probeBudget,
        Func<string, string, string, CancellationToken, Task<ProbeResult>> probe,
        CancellationToken cancellationToken)
    {
        var items = store.List().OrderBy(i => i.CreatedAt).ToList();
        var report = new ScanPassReport(0, 0, 0);
        if (items.Count < 2)
        {
            return report;
        }

        foreach (var reference in items)
        {
            foreach (var candidate in CandidatesFor(reference, items, pairs))
            {
                if (report.Probes >= probeBudget)
                {
                    return report;
                }
                if (pairs.Pending.Any(p =>
                        (p.A == reference.Id && p.B == candidate.Id) ||
                        (p.A == candidate.Id && p.B == reference.Id)))
                {
                    continue; // уже в очереди на разрешение — пробу не тратим
                }

                report = report with { Probes = report.Probes + 1 };
                var histOverlap = PairOverlap(reference, candidate);
                var prompt = BuildPairPrompt(reference.Content, candidate.Content);
                var position = await probe(prompt, reference.Id, candidate.Id, cancellationToken);
                var judgement = Judge(position);
                if (judgement.Kind == Verdict.Distinct)
                {
                    pairs.MarkDistinct(reference.Id, candidate.Id);
                    report = report with { MarkedDistinct = report.MarkedDistinct + 1 };
                }
                else
                {
                    // Similar и Uncertain копятся в очереди с полными факторами решения:
                    // схожесть гистограмм + распределение по цифрам 0-9.
                    pairs.AddPending(reference.Id, candidate.Id, histOverlap, judgement.Distribution);
                    if (judgement.Kind == Verdict.Similar)
                    {
                        report = report with { QueuedSimilar = report.QueuedSimilar + 1 };
                    }
                }
            }
        }
        return report;
    }
}
