using System.Text.RegularExpressions;
using QwenPlayground.Core.Probes;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Memory;

/// <summary>Результат реколла: факт + релевантность текущему вектору диалога.</summary>
public sealed record MemoryHit(MemoryItem Item, double Score);

/// <summary>
/// Ассоциативный реколл без эмбеддингов — две фазы (как NekoBot SearchSemanticRequest):
/// 1) вектор диалога классифицируется пробой компаньон-модели (это и есть наш аналог embedding),
///    каждый факт скорится overlap'ом гистограмм (категории 0.7 + эмодзи 0.3), без слоёв —
///    текстовым overlap'ом (Dice); берутся Top-X выше порога;
/// 2) rerank (аналог reranker'а): та же модель выбирает из Top-X самые релевантные диалогу
///    (multichoice с опцией None) — снимает зависимость от порога и переупорядочивает.
/// Один и тот же LLM выступает и embedding'ом, и reranker'ом, а запрос задаётся напрямую.
/// </summary>
public static class MemoryRecall
{


    public static async Task<IReadOnlyList<MemoryHit>> RecallAsync(
        string dialogueContext,
        MemoryStore store,
        string endpoint,
        int topX = 3,
        double minScore = 0.12,
        bool rerank = false,
        CancellationToken cancellationToken = default)
    {
        var queryLayers = await MemoryClassifier.ClassifyAsync(dialogueContext, endpoint, cancellationToken: cancellationToken);
        var hits = new List<MemoryHit>();

        foreach (var item in store.List())
        {
            var score = item.HasSemanticLayers
                ? ScoreSemantic(queryLayers, item.CategoryLayers, item.EmojiLayers)
                : ScoreText(dialogueContext, item.Content);
            if (score >= minScore)
            {
                hits.Add(new MemoryHit(item, score));
            }
        }

        var top = hits.OrderByDescending(h => h.Score).Take(topX).ToList();

        if (rerank && top.Count > 0)
        {
            try
            {
                var reranked = await RerankAsync(dialogueContext, top, endpoint, cancellationToken);
                return reranked;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // rerank упал — откат к pass 1 (best-effort)
            }
        }
        return top;
    }

    /// <summary>SecondPass: multichoice-проба по кандидатам pass 1, возвращает выбранные в порядке выбора.</summary>
    public static async Task<IReadOnlyList<MemoryHit>> RerankAsync(
        string dialogueContext, IReadOnlyList<MemoryHit> candidates, string endpoint,
        CancellationToken cancellationToken = default)
    {
        var prompt = MemoryClassifier.BuildRerankPrompt(dialogueContext, candidates);
        var settings = AppSettings.Get();
        var positions = await LlmProbeClient.NativeProbePositionsAsync(
            endpoint, prompt, nProbs: settings.MemoryRerankNProbs, nPredict: settings.MemoryRerankNPredict,
            stop: MemoryClassifier.ClassificationStopTokens, cancellationToken);

        var chosen = MemoryClassifier.ParseRerankLetters(positions, candidates.Count + 1);
        var result = new List<MemoryHit>();
        foreach (var index in chosen)
        {
            if (index < candidates.Count)
            {
                result.Add(candidates[index]);
            }
        }
        return result;
    }

    /// <summary>
    /// Semantic overlap: пересечение распределений категорий (Σ min) + эмодзи (Σ min),
    /// каждое нормировано суммой запроса; взвешено 0.7/0.3 — категории главный сигнал,
    /// эмодзи-вайб редкий, но сильный (как в NekoBot).
    /// </summary>
    public static double ScoreSemantic(
        MemorySemanticLayers query, IReadOnlyDictionary<string, double> memoryCategories, IReadOnlyDictionary<string, double> memoryEmoji,
        double? categoryWeight = null, double? emojiWeight = null)
    {
        var cw = categoryWeight ?? AppSettings.Get().MemoryCategoryWeight;
        var ew = emojiWeight ?? AppSettings.Get().MemoryEmojiWeight;
        var category = NormalizedOverlap(query.Categories, memoryCategories);
        var emoji = NormalizedOverlap(query.Emoji, memoryEmoji);
        return category * cw + emoji * ew;
    }

    /// <summary>Σ min распределений, нормированное суммой запроса (0..1).</summary>
    public static double NormalizedOverlap(
        IReadOnlyDictionary<string, double> query, IReadOnlyDictionary<string, double> memory)
    {
        if (query.Count == 0)
        {
            return 0;
        }
        var sum = 0.0;
        foreach (var (key, queryP) in query)
        {
            if (memory.TryGetValue(key, out var memoryP))
            {
                sum += Math.Min(queryP, memoryP);
            }
        }
        var querySum = query.Values.Sum();
        return querySum > 0 ? sum / querySum : 0;
    }

    /// <summary>Текстовый overlap (Dice по токенам) — фолбэк для фактов без семантических слоёв.</summary>
    public static double ScoreText(string query, string content)
    {
        var a = Tokenize(query);
        var b = Tokenize(content);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }
        var bSet = b.ToHashSet();
        var intersection = a.Count(token => bSet.Contains(token));
        return 2.0 * intersection / (a.Count + b.Count);
    }

    private static readonly Regex TokenPattern = new("[^\\p{L}\\p{N}]+", RegexOptions.Compiled);

    private static List<string> Tokenize(string text) =>
        TokenPattern.Split((text ?? string.Empty).ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();
}
