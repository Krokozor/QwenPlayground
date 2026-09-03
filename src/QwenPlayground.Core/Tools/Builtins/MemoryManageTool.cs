using System.IO;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Probes;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Единая точка менеджмента памяти — без множения тулов под каждую операцию.
/// Действия:
///  - pairs       — показать пары-кандидаты (отсортированы по мутности: сначала самые неопределённые);
///  - inspect     — полная детализация одной пары: контент + распределение по цифрам 0-9;
///  - probe       — ручная оценка пары: прогоняет два факта через классификатор, возвращает распределение;
///  - scan        — ручной запуск сканера дубликатов (один проход, бюджет 10 проб);
///  - not_similar — развести пару (false positive): классификатор больше не предложит;
///  - merge       — слияние двух фактов в один;
///  - delete      — удалить факт по id;
///  - clear       — сбросить всю очередь Pending (для перегенерации сканером).
/// </summary>
[Tool("memory_manage",
    "Memory maintenance hub. Actions: 'pairs' — list similarity pairs sorted by murkiness; " +
    "'inspect' — full detail on one pair (content + digit distribution); " +
    "'probe' — evaluate a specific pair via classifier (returns full distribution, caches result); " +
    "'scan' — manual scanner pass (10 probes); " +
    "'not_similar' — mark pair as NOT similar (reject); " +
    "'merge' — combine two facts (provide MergedContent for agent-synthesized result); " +
    "'delete' — remove a fact; " +
    "'clear' — reset all pending pairs.")]
public sealed class MemoryManageTool : AgentTool
{
    [ToolParameter("Action: pairs | inspect | probe | scan | not_similar | merge | delete | clear", Required = true)]
    public string Action { get; set; } = string.Empty;

    [ToolParameter("First memory id (for inspect / not_similar / merge / delete)")]
    public string IdA { get; set; } = string.Empty;

    [ToolParameter("Second memory id (for inspect / not_similar / merge)")]
    public string IdB { get; set; } = string.Empty;

    [ToolParameter("Synthesized content for the merged fact (for 'merge' action). If empty, falls back to concatenation.")]
    public string MergedContent { get; set; } = string.Empty;

    private const int PairsPreviewLimit = 20;
    private const int ContentPreviewLimit = 200;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (!AppSettings.Get().MemoryEnabled)
        {
            return MemoryToolGate.DisabledMessage;
        }
        var store = new MemoryStore();
        var pairs = new PairsStore(store.Root);

        switch (Action.Trim().ToLowerInvariant())
        {
            case "pairs":
                return FormatPendingPairs(store, pairs);

            case "inspect":
                return FormatInspect(store, pairs);

            case "probe":
                return await ProbePairAsync(store, pairs, cancellationToken);

            case "scan":
                return await ScanAsync(store, pairs, cancellationToken);

            case "not_similar":
                if (!ValidPair(store, pairs, out var distinctError))
                {
                    return $"memory_manage not_similar: {distinctError}";
                }
                pairs.MarkDistinct(IdA.Trim(), IdB.Trim());
                return $"Marked {IdA.Trim()} ~ {IdB.Trim()} as NOT similar. The classifier will not propose this pair again.";

            case "merge":
            {
                if (!ValidPair(store, pairs, out var mergeError))
                {
                    return $"memory_manage merge: {mergeError}";
                }
                var idA = IdA.Trim();
                var idB = IdB.Trim();
                var itemA = store.Get(idA)!;
                var itemB = store.Get(idB)!;
                var content = !string.IsNullOrWhiteSpace(MergedContent)
                    ? MergedContent.Trim()
                    : itemA.Content + "\n" + itemB.Content;
                var merged = store.Add(content, source: "merge");
                store.Remove(idA);
                store.Remove(idB);
                await MemoryClassifier.EnrichAsync(
                    merged, AppSettings.Get().CompanionEndpoint, cancellationToken: cancellationToken);
                if (merged.HasSemanticLayers)
                {
                    store.Update(merged);
                }
                pairs.Cleanup(store.List().Select(i => i.Id));
                return $"Merged {idA[..8]} + {idB[..8]} → {merged.Id[..8]}. " +
                       $"Filed as: {MemoryClassifier.TopName(merged.CategoryLayers)} {MemoryClassifier.TopEmojiOf(merged.EmojiLayers)}. " +
                       $"Content: {(MergedContent.Length > 0 ? "agent-synthesized" : "concatenated")}.";
            }

            case "delete":
            {
                if (IdA.Trim().Length == 0)
                {
                    return "memory_manage delete: provide IdA.";
                }
                var deleter = new MemoryDeleteTool { Id = IdA.Trim() };
                var result = await deleter.ExecuteAsync(context, cancellationToken);
                pairs.Cleanup(store.List().Select(i => i.Id));
                return result;
            }

            case "clear":
            {
                var count = pairs.Pending.Count;
                pairs.ClearPending();
                return $"Cleared {count} pending pairs. The scanner will regenerate on next heartbeat.";
            }

            default:
                return "memory_manage: unknown action. Use: pairs | inspect | not_similar | merge | delete | clear.";
        }
    }

    private bool ValidPair(MemoryStore store, PairsStore pairs, out string error)
    {
        var a = IdA.Trim();
        var b = IdB.Trim();
        if (a.Length == 0 || b.Length == 0 || a == b)
        {
            error = "provide two different, non-empty ids.";
            return false;
        }
        if (store.Get(a) is null || store.Get(b) is null)
        {
            error = "one of the ids not found in memories/.";
            return false;
        }
        if (pairs.IsDistinct(a, b))
        {
            error = "this pair is already marked as not similar.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Ручная оценка конкретной пары: прогоняет через классификатор, сохраняет результат в Pending,
    /// возвращает полное распределение. Если пара уже в Pending — возвращает сохранённые данные без ре-пробы.
    /// </summary>
    private async Task<string> ProbePairAsync(MemoryStore store, PairsStore pairs, CancellationToken ct)
    {
        var a = IdA.Trim();
        var b = IdB.Trim();
        if (a.Length == 0 || b.Length == 0 || a == b)
        {
            return "memory_manage probe: provide two different non-empty ids (IdA, IdB).";
        }
        var itemA = store.Get(a);
        var itemB = store.Get(b);
        if (itemA is null || itemB is null)
        {
            return $"memory_manage probe: one or both ids not found.";
        }

        // Если уже в Pending — не тратим пробу, возвращаем сохранённое.
        var existing = pairs.Pending.FirstOrDefault(p =>
            (p.A == a && p.B == b) || (p.A == b && p.B == a));
        if (existing is not null)
        {
            return FormatProbeResult(itemA, itemB, existing, cached: true);
        }

        // Если в Distinct — не трогаем.
        if (pairs.IsDistinct(a, b))
        {
            return "memory_manage probe: this pair is marked as Distinct (not similar). Use 'not_similar' undo or clear to re-evaluate.";
        }

        // Компаньон-модель не настроена — пробу некуда слать (не best-effort, сообщаем явно).
        if (!AppSettings.CompanionConfigured)
        {
            return "memory_manage probe: companion model not configured (CompanionEndpoint empty). Set it in Settings → Memory → Model for probes.";
        }

        // Пробуем.
        var endpoint = AppSettings.Get().CompanionEndpoint;
        var prompt = MemorySimilarity.BuildPairPrompt(itemA.Content, itemB.Content);
        var position = await LlmProbeClient.ProbeAsync(endpoint, prompt, nProbs: 20, ct);
        var judgement = MemorySimilarity.Judge(position);
        var histOverlap = MemorySimilarity.PairOverlap(itemA, itemB);

        // Сохраняем в Pending (если не Distinct по вердикту).
        if (judgement.Kind == MemorySimilarity.Verdict.Distinct)
        {
            pairs.MarkDistinct(a, b);
            return FormatProbeResult(itemA, itemB, null, cached: false, verdict: "Distinct (auto-marked, not queued)");
        }
        pairs.AddPending(a, b, histOverlap, judgement.Distribution);
        return FormatProbeResult(itemA, itemB,
            new PendingPair(a, b, histOverlap, judgement.Distribution), cached: false);
    }

    /// <summary>Ручной запуск сканера: один проход с бюджетом 10 проб.</summary>
    private async Task<string> ScanAsync(MemoryStore store, PairsStore pairs, CancellationToken ct)
    {
        // Компаньон-модель не настроена — сканеру нечего прогонять (не best-effort, сообщаем явно).
        if (!AppSettings.CompanionConfigured)
        {
            return "memory_manage scan: companion model not configured (CompanionEndpoint empty). Set it in Settings → Memory → Model for probes.";
        }

        var endpoint = AppSettings.Get().CompanionEndpoint;
        var report = await MemorySimilarity.ScanPassAsync(
            store, pairs, endpoint, probeBudget: 10,
            async (prompt, refId, candId, c) =>
                await LlmProbeClient.ProbeAsync(endpoint, prompt, nProbs: 20, c),
            ct);
        return $"Scan complete: {report.Probes} probes → queued: {report.QueuedSimilar}, distinct: {report.MarkedDistinct}. " +
               $"Total pending: {pairs.Pending.Count}.";
    }

    private static string FormatProbeResult(
        MemoryItem a, MemoryItem b, PendingPair? pair, bool cached, string? verdict = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== Probe: {a.Id[..8]} ~ {b.Id[..8]} ===");
        if (cached)
        {
            sb.AppendLine("(cached — not re-probed)");
        }
        if (verdict is not null)
        {
            sb.AppendLine($"Verdict: {verdict}");
        }
        if (pair is not null)
        {
            sb.AppendLine($"HistOverlap: {pair.HistOverlap:0.000}");
            sb.AppendLine($"Score: {pair.Score:0.00}  Entropy: {pair.Entropy:0.00}  Argmax: {pair.Argmax}");
            sb.AppendLine("Distribution:");
            for (var d = 0; d < 10; d++)
            {
                double p = (pair.DigitDist is not null && pair.DigitDist.Length > d) ? pair.DigitDist[d] : 0.0;
                int barLen = (int)(p * 40.0);
                sb.AppendLine("  " + d + ": " + p.ToString("0.000") + " " + new string('#', barLen));
            }
        }
        sb.AppendLine();
        sb.AppendLine($"A: {Truncate(a.Content, 150)}");
        sb.AppendLine($"B: {Truncate(b.Content, 150)}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Список пар, отсортированный по мутности (наиболее неопределённые первыми).</summary>
    private static string FormatPendingPairs(MemoryStore store, PairsStore pairs)
    {
        if (pairs.Pending.Count == 0)
        {
            return "No similarity pairs pending. Memory is consolidated.";
        }
        var sorted = pairs.Pending.OrderByDescending(p => p.Attention).ToList();
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Similarity pairs awaiting decision: {pairs.Pending.Count} " +
                           "(sorted by Attention — most thought-provoking first).");
        builder.AppendLine("Use 'inspect' with IdA/IdB for full distribution detail.");
        builder.AppendLine();
        foreach (var pair in sorted.Take(PairsPreviewLimit))
        {
            var itemA = store.Get(pair.A);
            var itemB = store.Get(pair.B);
            builder.Append(pair.A[..8]).Append(" ~ ").Append(pair.B[..8]).Append(' ')
                   .AppendLine($"(att {pair.Attention:0.00}, score {pair.Score:0.0}, H {pair.Entropy:0.00}, gap {pair.PeakGap}, bim {pair.SecondPeakRatio:0.00})");
            builder.Append("  dist: [").Append(FormatDist(pair.DigitDist)).Append(']');
            builder.Append("  A: ").Append(Truncate(itemA?.Content, 80)).Append('\n');
            builder.Append("       B: ").Append(Truncate(itemB?.Content, 80)).Append('\n');
        }
        if (pairs.Pending.Count > PairsPreviewLimit)
        {
            builder.AppendLine($"…and {pairs.Pending.Count - PairsPreviewLimit} more.");
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>Полная инспекция одной пары: контент + распределение + метрики.</summary>
    private string FormatInspect(MemoryStore store, PairsStore pairs)
    {
        var a = IdA.Trim();
        var b = IdB.Trim();
        if (a.Length == 0 || b.Length == 0)
        {
            return "memory_manage inspect: provide IdA and IdB.";
        }
        var itemA = store.Get(a);
        var itemB = store.Get(b);
        if (itemA is null || itemB is null)
        {
            return $"memory_manage inspect: one or both ids not found ({(itemA is null ? a : "ok")} / {(itemB is null ? b : "ok")}).";
        }

        var pair = pairs.Pending.FirstOrDefault(p =>
            (p.A == a && p.B == b) || (p.A == b && p.B == a));

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"=== Pair Inspection: {a} ~ {b} ===");
        builder.AppendLine();

        if (pair is not null)
        {
            builder.AppendLine("── Classifier ──");
            builder.AppendLine($"  Histogram overlap: {pair.HistOverlap:0.000}");
            builder.AppendLine($"  Score (weighted):  {pair.Score:0.00}");
            builder.AppendLine($"  Entropy (bits):    {pair.Entropy:0.00}");
            builder.AppendLine($"  Argmax digit:      {pair.Argmax}");
            builder.AppendLine($"  Attention:         {pair.Attention:0.00}");
            builder.AppendLine($"  PeakGap:           {pair.PeakGap}");
            builder.AppendLine($"  SecondPeakRatio:   {pair.SecondPeakRatio:0.00}");
            builder.AppendLine();
            builder.AppendLine("  Distribution [0..9]:");
            for (var d = 0; d < 10; d++)
            {
                double p = (pair.DigitDist is not null && pair.DigitDist.Length > d) ? pair.DigitDist[d] : 0.0;
                int barLen = (int)(p * 40.0);
                var bar = new string('#', barLen);
                builder.AppendLine("    " + d + ": " + p.ToString("0.000") + " " + bar);
            }
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("(pair not in Pending queue — no classifier data)");
            builder.AppendLine();
        }

        builder.AppendLine("── Fact A ──");
        builder.AppendLine($"  id: {itemA.Id}");
        builder.AppendLine($"  created: {itemA.CreatedAt:yyyy-MM-dd HH:mm}");
        builder.AppendLine($"  content: {itemA.Content}");
        builder.AppendLine();
        builder.AppendLine("── Fact B ──");
        builder.AppendLine($"  id: {itemB.Id}");
        builder.AppendLine($"  created: {itemB.CreatedAt:yyyy-MM-dd HH:mm}");
        builder.AppendLine($"  content: {itemB.Content}");

        return builder.ToString().TrimEnd();
    }

    /// <summary>Компактное распределение: "[0.05 0.10 0.15 ...]"</summary>
    private static string FormatDist(double[]? dist)
    {
        if (dist is null || dist.Length != 10)
        {
            return "N/A";
        }
        return string.Join(" ", dist.Select(p => p.ToString("0.00")));
    }

    private static string Truncate(string? text, int maxLen)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(empty)";
        }
        var oneLine = text.Replace("\r\n", " ").Replace('\n', ' ');
        return oneLine.Length <= maxLen ? oneLine : oneLine[..maxLen] + "…";
    }
}
