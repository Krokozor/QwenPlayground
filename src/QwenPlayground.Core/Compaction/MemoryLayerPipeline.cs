using QwenPlayground.Core.Memory;

namespace QwenPlayground.Core.Compaction;

/// <summary>
/// Конвейер слоистой памяти (L1/L2/L3) main-агента — аналог памяти Gemma4:
/// постепенное растворение контекста тремя слоями глубины, каждый шаг — изолированный
/// LLM-вызов (в контекст попадает только релевантный материал, без всего чата).
///
/// Шаги (по дизайну владельца):
///   1. merge [L1, L2] → Temp — ТОЛЬКО когда оба слоя непустые (все три на месте);
///   2. валидация [L1+L2] vs [Temp] → утерянные факты → в ассоциативную память;
///   3. ротация — каскад по глубине, к моменту применения ничего не теряется:
///        · L1 пуст (тёплый ап-фейз) → СДВИГ без модели: L2→L1, L3→L2;
///        · все три заполнены         → Temp→L1 (после валидации), старый L3→L2;
///        · пустые слоты заполняются сверху: нет L2 — сдвигаем L3;
///   4. [сегмент 50% чата] → L3    — изолированный контекст: только транскрипт сегмента;
///   5. сверка [сегмент] vs [L3] → ещё утерянные факты → в память.
///
/// Best-effort на каждом шаге: упавший шаг не роняет остальные, факты из валидаций
/// собираются сколько получилось. Но РОТАЦИЯ применяется только при полном успехе
/// (см. MergeSucceeded/SegmentSucceeded): иначе слои остаются на месте нетронутыми
/// (старый L3 никогда не перетирается) — вызывающий прерывает компакцию, бэкап уже снят.
/// </summary>
public static class MemoryLayerPipeline
{
    public sealed class Result
    {
        public LayerMemory Next { get; init; } = new();
        public List<string> Facts { get; init; } = new();
        /// <summary>Слияние L1+L2 прошло (тривиально true, если сливать было нечего).</summary>
        public bool MergeSucceeded { get; init; }
        /// <summary>Суммаризация сегмента прошла.</summary>
        public bool SegmentSucceeded { get; init; }
    }

    public static async Task<Result> RunAsync(
        LayerMemory current,
        string transcript,
        Func<string, CancellationToken, Task<string>> complete,
        Action<string>? onStage = null,
        CancellationToken cancellationToken = default)
    {
        // Дизайн владельца: слияние L1+L2 нужно ТОЛЬКО когда L1 заполнен (все три слоя на месте).
        // Тёплый ап-фейз (L1 пуст) — каскадный сдвиг вверх БЕЗ модели: L2→L1, L3→L2.
        var hasL1 = !string.IsNullOrWhiteSpace(current.L1);
        var hasL2 = !string.IsNullOrWhiteSpace(current.L2);
        var hasL3 = !string.IsNullOrWhiteSpace(current.L3);
        var mergeNeeded = hasL1 && hasL2;

        var facts = new List<string>();

        // 1. merge L1+L2 → Temp (изолированный контекст: только L1+L2).
        string? mergedTemp = null;
        var mergeSucceeded = !mergeNeeded; // слияния нет — тривиально успешно
        if (mergeNeeded)
        {
            onStage?.Invoke("слияние L1+L2");
            mergedTemp = await TryCompleteAsync(BuildMergePrompt(current.L1, current.L2), complete, cancellationToken);
            mergeSucceeded = !string.IsNullOrWhiteSpace(mergedTemp);
            if (mergeSucceeded)
            {
                // 2. валидация мерджа: не потеряли ли важное между L1+L2 и Temp.
                onStage?.Invoke("сверка слияния L1+L2");
                var validation = await TryCompleteAsync(
                    BuildMergeValidationPrompt(current.L1, current.L2, mergedTemp!), complete, cancellationToken);
                facts.AddRange(MemoryExtractor.ParseFacts(validation ?? string.Empty));
            }
        }

        // 4. сегмент → новое L3 (изолированный контекст: только транскрипт сегмента).
        onStage?.Invoke("суммаризация сегмента");
        var segmentSummary = await TryCompleteAsync(BuildSegmentSummaryPrompt(transcript), complete, cancellationToken);
        var segmentSucceeded = !string.IsNullOrWhiteSpace(segmentSummary);
        if (segmentSucceeded)
        {
            // 5. сверка сегмента и L3: не потеряли ли важное.
            onStage?.Invoke("сверка сегмента с резюме");
            var validation = await TryCompleteAsync(
                BuildSegmentValidationPrompt(transcript, segmentSummary!), complete, cancellationToken);
            facts.AddRange(MemoryExtractor.ParseFacts(validation ?? string.Empty));
        }

        // 3. Ротация — только когда ничего не теряется:
        //   · нет нового L3 (сегмент провалился)         → все слои остаются на месте;
        //   · merge был нужен и упал                     → L2 нельзя поглотить, каскад невозможен
        //     (старый L3 при этом не выкидываем!) → слои остаются на месте, компакция прервётся;
        //   · merge не нужен (L1 пуст)                   → каскад дословно: L2→L1, L3→L2;
        //   · полный комплект, всё ок                    → Temp→L1, старый L3→L2.
        var canCommit = segmentSucceeded && mergeSucceeded;
        if (canCommit)
        {
            return new Result
            {
                Next = new LayerMemory
                {
                    L1 = mergeNeeded
                        ? mergedTemp!
                        : hasL2 ? current.L2.Trim() : current.L1.Trim(),
                    L2 = hasL3 ? current.L3.Trim() : current.L2.Trim(),
                    L3 = segmentSummary!
                },
                Facts = facts,
                MergeSucceeded = mergeSucceeded,
                SegmentSucceeded = segmentSucceeded
            };
        }

        return new Result
        {
            Next = new LayerMemory { L1 = current.L1.Trim(), L2 = current.L2.Trim(), L3 = current.L3.Trim() },
            Facts = facts,
            MergeSucceeded = mergeSucceeded,
            SegmentSucceeded = segmentSucceeded
        };
    }

    private static async Task<string?> TryCompleteAsync(
        string prompt, Func<string, CancellationToken, Task<string>> complete, CancellationToken cancellationToken)
    {
        try
        {
            var result = (await complete(prompt, cancellationToken)).Trim();
            return result.Length > 0 ? result : null;
        }
        catch (OperationCanceledException)
        {
            // Отмена конвейера — не «шаг провалился»: прерываем сразу, а не проходим
            // остальные шаги с уже отменённым токеном.
            throw;
        }
        catch (Exception)
        {
            return null; // best-effort: шаг не критичен сам по себе
        }
    }

    public static string BuildMergePrompt(string l1, string l2)
    {
        var templates = PromptCatalog.Load();
        return PromptTemplateSet.Render(templates.Merge, new Dictionary<string, string>
        {
            ["l1"] = string.IsNullOrWhiteSpace(l1) ? "(empty)" : l1.Trim(),
            ["l2"] = string.IsNullOrWhiteSpace(l2) ? "(empty)" : l2.Trim()
        });
    }

    public static string BuildMergeValidationPrompt(string l1, string l2, string temp)
    {
        var templates = PromptCatalog.Load();
        return PromptTemplateSet.Render(templates.MergeValidation, new Dictionary<string, string>
        {
            ["l1"] = l1.Trim(),
            ["l2"] = l2.Trim(),
            ["temp"] = temp.Trim()
        });
    }

    public static string BuildSegmentSummaryPrompt(string transcript)
    {
        var templates = PromptCatalog.Load();
        return PromptTemplateSet.Render(templates.SegmentSummary, new Dictionary<string, string>
        {
            ["transcript"] = transcript
        });
    }

    public static string BuildSegmentValidationPrompt(string transcript, string l3)
    {
        var templates = PromptCatalog.Load();
        return PromptTemplateSet.Render(templates.SegmentValidation, new Dictionary<string, string>
        {
            ["transcript"] = transcript,
            ["l3"] = l3.Trim()
        });
    }
}