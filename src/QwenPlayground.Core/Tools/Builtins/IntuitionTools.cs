using System.Text;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Probes;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Общий раннер пробы «интуиции»: проверка, что сервисный слот свободен (не идёт
/// компакция/суммаризация), затем SelfProbePositionsAsync (erase + пиннинг в
/// SlotAllocation.Service + /completion с n_probs). Проба ~1-2 с, KV чат-слота не трогается
/// (пиннутый слот не ходит в RAM prompt cache). Ошибки — человекочитаемым текстом:
/// тул best-effort, «недоступно» — это ответ, а не сбой хода.
///
/// nPredict = K+1, где K — сколько распределений нужно: сервер записывает окно по каждому
/// сгенерированному токену, кроме последнего (проверено живой пробой; n_predict=1 → поле
/// completion_probabilities отсутствует вовсе).
/// </summary>
internal static class IntuitionProbeRunner
{
    public static async Task<(IReadOnlyList<ProbeResult>? Positions, string? Error)> RunAsync(
        string prompt, int nProbs, int nPredict, CancellationToken cancellationToken)
    {
        try
        {
            var kv = new KvCacheController();
            var slots = await kv.GetSlotsAsync(cancellationToken);
            if (slots.FirstOrDefault(s => s.Id == SlotAllocation.Service) is { IsProcessing: true })
            {
                return (null,
                    "Service slot (3) is busy (compaction/summarization in progress) — retry the probe in a few seconds.");
            }
            var positions = await LlmProbeClient.SelfProbePositionsAsync(
                prompt, nProbs, nPredict, stop: new[] { QwenSpecialTokens.ImEnd }, cancellationToken);
            return (positions, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (null, $"Probe failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Сгенерированный токен позиции: позиционный уровень (цел для мультИБайтовых токенов),
    /// fallback на argmax окна (для ASCII они совпадают при top_k=1).
    /// </summary>
    public static string GeneratedToken(ProbeResult position) =>
        !string.IsNullOrEmpty(position.PositionToken) ? position.PositionToken : position.ArgmaxToken;

    /// <summary>
    /// Debug-режим (временный): сырые данные пробы — все записанные позиции, токены с
    /// codepoint'ами, logprob/энтропия, топ-5 окна. Показывает всю сгенерированную
    /// последовательность (включая \n) и где модель остановилась.
    /// </summary>
    public static string FormatDebug(IReadOnlyList<ProbeResult> positions)
    {
        var sb = new StringBuilder();
        sb.Append($"[debug] positions recorded: {positions.Count} (server records every generated token except the last)\n");
        for (var i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            var token = GeneratedToken(p);
            sb.Append($"  [{i}] token=[{token}] ({Codepoints(token)}) p={Math.Exp(p.ArgmaxLogProb):0.###} H={p.Entropy:0.##}\n");
            sb.Append("      window: " + string.Join(", ", p.TopTokens.Take(5)
                .Select(t => $"\"{t.Token}\"({Codepoints(t.Token)})={Math.Exp(t.LogProb):0.###}")) + "\n");
        }
        sb.Append("[debug] last generated token not recorded (EOS/stop hit or n_predict cap)");
        return sb.ToString();
    }

    private static string Codepoints(string s) =>
        string.Join(",", s.Select(c => "U+" + ((int)c).ToString("X4")));

    /// <summary>
    /// Накопление эмодзи-распределения по позициям пробы (аналог AccumulateLayers памяти,
    /// но с реконструкцией кандидатов). Сервер генерирует эмодзи побайтово и записывает
    /// позицию на ПОСЛЕДНЕМ байте символа: окно позиции = распределение по последнему байту
    /// (префикс уже зафиксирован). Кандидат = префикс выбранного символа + байт кандидата
    /// (id → байт через byteResolver). Софтмакс внутри окна, масса по эмодзи, итог в 1.
    /// </summary>
    public static Dictionary<string, double> AccumulateEmoji(
        IReadOnlyList<ProbeResult> positions, Func<int, int?> byteResolver)
    {
        var masses = new Dictionary<string, double>();
        foreach (var position in positions)
        {
            var chosen = GeneratedToken(position).Trim();
            if (!MemoryCategories.IsEmojiToken(chosen))
            {
                continue;
            }
            var prefix = PrefixBytes(chosen);
            var max = position.TopTokens.Max(t => t.LogProb);
            var sum = position.TopTokens.Sum(t => Math.Exp(t.LogProb - max));
            if (sum <= 0)
            {
                continue;
            }
            foreach (var token in position.TopTokens)
            {
                var p = Math.Exp(token.LogProb - max) / sum;
                var b = byteResolver(token.Id);
                if (b is null)
                {
                    continue; // не байт-токен (merged/спец) — реконструировать нельзя
                }
                var candidate = ConcatByte(prefix, b.Value);
                if (candidate is null || !MemoryCategories.IsEmojiToken(candidate))
                {
                    continue;
                }
                masses[candidate] = masses.GetValueOrDefault(candidate) + p;
            }
        }
        var total = masses.Values.Sum();
        return total > 0
            ? masses.ToDictionary(kv => kv.Key, kv => kv.Value / total)
            : new Dictionary<string, double>();
    }

    /// <summary>UTF-8-байты символа без последнего (префикс, зафиксированный до позиции).</summary>
    public static byte[] PrefixBytes(string chosen)
    {
        var bytes = Encoding.UTF8.GetBytes(chosen);
        return bytes.Length > 1 ? bytes[..^1] : [];
    }

    /// <summary>Префикс + последний байт → символ (null, если байты не дают валидный UTF-8).</summary>
    public static string? ConcatByte(byte[] prefix, int lastByte)
    {
        var full = new byte[prefix.Length + 1];
        prefix.CopyTo(full, 0);
        full[^1] = (byte)lastByte;
        var s = Encoding.UTF8.GetString(full);
        return s.Contains('\uFFFD') ? null : s;
    }

    /// <summary>
    /// Debug-дамп vibe-пробы с реконструкцией кандидатов окна (id → байт → эмодзи).
    /// </summary>
    public static string FormatVibeDebug(IReadOnlyList<ProbeResult> positions, Func<int, int?> byteResolver)
    {
        var sb = new StringBuilder();
        sb.Append($"[debug vibe] positions: {positions.Count}; decoder loaded: {QwenPlayground.Core.Tokenizers.QwenVocabDecoder.IsLoaded}\n");
        for (var i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            var token = GeneratedToken(p);
            var chosen = token.Trim();
            sb.Append($"  [{i}] chosen=[{chosen}] ({Codepoints(chosen)}) p={Math.Exp(p.ArgmaxLogProb):0.###}\n");
            if (!MemoryCategories.IsEmojiToken(chosen))
            {
                sb.Append("      (not an emoji — position skipped in accumulation)\n");
                continue;
            }
            var prefix = PrefixBytes(chosen);
            sb.Append("      prefix=[" + string.Join(" ", prefix.Select(b => b.ToString("X2"))) + "] candidates:\n");
            foreach (var t in p.TopTokens.Take(8))
            {
                var b = byteResolver(t.Id);
                var bHex = b is null ? "?" : b.Value.ToString("X2");
                var candidate = b is null ? null : ConcatByte(prefix, b.Value);
                var label = candidate is null
                    ? $"id={t.Id} byte={bHex} → <not emoji/undecodable>"
                    : $"{candidate} (byte {bHex})";
                sb.Append($"        {label} = {Math.Exp(t.LogProb):0.###}\n");
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Интуиция-1: multichoice по вариантам. Модель перечисляет буквы вариантов (одиночные
/// ASCII-токены); каждая записанная позиция = буква + её вероятность + распределение по ней.
/// </summary>
[Tool("intuition_choice",
    "Ask your own intuition a multiple-choice question via logprob probe (no full generation): " +
    "the model lists option letters, each with its probability. Use for a fast gut check among " +
    "concrete alternatives — cheaper and less noisy than a full turn. Runs on your own model in " +
    "the service slot (~1-2s, does not touch your chat KV).",
    ToolGroup.Intuition)]
public sealed class IntuitionChoiceTool : AgentTool
{
    [ToolParameter("The question to ask.", Required = true)]
    public string Question { get; set; } = string.Empty;

    [ToolParameter("The options to choose from (2-9 items). A 'None of the above' option is added automatically.", Required = true)]
    public string[] Options { get; set; } = [];

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Question))
        {
            return "intuition_choice: the question is empty.";
        }
        var options = Options
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim())
            .ToList();
        if (options.Count < 2)
        {
            return "intuition_choice: need at least 2 options.";
        }
        if (options.Count > 9)
        {
            return "intuition_choice: too many options (max 9).";
        }

        var noneIndex = options.Count;
        var optionCount = options.Count + 1;
        var (positions, error) = await IntuitionProbeRunner.RunAsync(
            IntuitionPrompts.BuildChoicePrompt(Question, options), nProbs: 20,
            nPredict: optionCount + 2, cancellationToken);
        if (positions is null)
        {
            return error!;
        }
        if (positions.Count == 0)
        {
            return "Intuition (choice): the model ended the answer immediately (no option).";
        }

        var sb = new StringBuilder();
        sb.Append("Intuition (choice): ");
        var names = new List<string>();
        foreach (var position in positions)
        {
            var letter = IntuitionProbeRunner.GeneratedToken(position).Trim();
            if (letter.Length != 1 || letter[0] < 'A' || letter[0] > 'A' + optionCount)
            {
                continue; // не буква варианта (например, \n)
            }
            var index = letter[0] - 'A';
            if (index > noneIndex)
            {
                continue;
            }
            var name = index < options.Count ? options[index] : "None of the above";
            if (names.Contains(name, StringComparer.Ordinal))
            {
                continue; // повтор
            }
            names.Add(name);
            sb.Append($"{letter}=\"{name}\" (p={Math.Exp(position.ArgmaxLogProb):0.##}, H={position.Entropy:0.##}) ");
        }
        if (names.Count == 0)
        {
            sb.Append("no clear option (no option letters in the answer)");
        }
        sb.Append('\n');

        var mode = AppSettings.Get().IntuitionFormat;
        var first = positions[0];
        // Распределение по буквам окна (нормировка по буквам вариантов, как AccumulateLayers
        // в памяти: масса считается только по «своим» токенам) — с именами вариантов, не буквами.
        var letterProb = first.TopTokens
            .Where(t => t.Token.Trim().Length == 1)
            .GroupBy(t => t.Token.Trim()[0])
            .ToDictionary(g => g.Key, g => g.Max(t => Math.Exp(t.LogProb)));
        var total = Enumerable.Range(0, optionCount)
            .Sum(i => letterProb.GetValueOrDefault((char)('A' + i), 0.0));
        var distribution = total > 0
            ? Enumerable.Range(0, optionCount)
                .Select(i => (Name: i < options.Count ? options[i] : "none of the above",
                              P: letterProb.GetValueOrDefault((char)('A' + i), 0.0) / total))
                .OrderByDescending(x => x.P)
                .Select(x => (Name: x.Name, Value: x.P))
                .ToList()
            : new List<(string Name, double Value)>();
        if (distribution.Count > 0)
        {
            sb.Append(IntuitionFormat.Distribution(mode, "Distribution", distribution));
        }
        if (AppSettings.Get().IntuitionDebugDump)
        {
            sb.Append('\n').Append(IntuitionProbeRunner.FormatDebug(positions));
        }
        return sb.ToString();
    }
}

/// <summary>
/// Интуиция-2: ординальная шкала 0-9 одним токен-цифрой: одно окно (распределение по цифре) —
/// взвешенный балл + энтропия = уверенность.
/// </summary>
[Tool("intuition_rating",
    "Rate a statement on an ordinal 0-9 scale via logprob probe (single digit token + distribution + " +
    "entropy = confidence). Use for quick gut-check judgments: likelihood, similarity, severity, " +
    "confidence in an assumption. Runs on your own model in the service slot (~1s, does not touch " +
    "your chat KV).",
    ToolGroup.Intuition)]
public sealed class IntuitionRatingTool : AgentTool
{
    [ToolParameter("The statement or question to rate.", Required = true)]
    public string Statement { get; set; } = string.Empty;

    [ToolParameter("Optional: what the scale means (what 0 and 9 stand for). Default: 0 = no/not at all, 9 = yes/to the maximum.")]
    public string Scale { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Statement))
        {
            return "intuition_rating: the statement is empty.";
        }

        var (positions, error) = await IntuitionProbeRunner.RunAsync(
            IntuitionPrompts.BuildRatingPrompt(Statement, Scale), nProbs: 20, nPredict: 2,
            cancellationToken);
        if (positions is null)
        {
            return error!;
        }
        if (positions.Count == 0)
        {
            return "Intuition (rating): the model ended the answer immediately (no digit).";
        }

        var (score, entropy, dist, found) = IntuitionPrompts.DigitDistribution(positions);
        if (!found)
        {
            return "Intuition (rating): no clear digit in the probe window — try a clearer statement.";
        }
        var digit = (int)Math.Round(score);
        var confidence = entropy < 1.0 ? "confident" : entropy < 2.0 ? "moderate" : "uncertain";

        // Полное распределение по 0-9 (нормировано, сумма 1) — как слои в памяти.
        var sb = new StringBuilder();
        sb.Append($"Intuition (rating): {score:0.##} → digit {digit} ({confidence}, entropy {entropy:0.##} bits)\n");
        sb.Append(IntuitionFormat.Distribution(
            AppSettings.Get().IntuitionFormat, "Distribution (0-9)",
            Enumerable.Range(0, 10).Select(d => (Name: d.ToString(), Value: dist[d]))));
        if (AppSettings.Get().IntuitionDebugDump)
        {
            sb.Append('\n').Append(IntuitionProbeRunner.FormatDebug(positions));
        }
        return sb.ToString();
    }
}

/// <summary>
/// Интуиция-3: эмодзи-вайб: модель выводит ровно 5 одиночных эмодзи (промпт с примером
/// формата). Сервер генерирует их побайтово и записывает позицию на последнем байте
/// символа: окно позиции = распределение по последнему байту → префикс + байт кандидата
/// (id через QwenVocabDecoder) = кандидат-эмодзи. Вывод: последовательность модели +
/// накопленное распределение по ВСЕМ эмодзи окон (аналог эмодзи-слоя памяти).
/// </summary>
[Tool("intuition_vibe",
    "Get an emoji 'vibe' for a text via logprob probe: the model's emoji sequence plus an " +
    "accumulated distribution over all emoji the model considered (its own model's gut " +
    "reaction, token by token). Use for a quick sentiment/mood check. Runs on your own model " +
    "in the service slot (~1-2s, does not touch your chat KV).",
    ToolGroup.Intuition)]
public sealed class IntuitionVibeTool : AgentTool
{
    [ToolParameter("The text to feel the vibe of.", Required = true)]
    public string Text { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            return "intuition_vibe: the text is empty.";
        }

        // Ровно 5 эмодзи подряд (промпт с примером формата) + возможные FE0F/хвостовые
        // токены; n_predict=16 — запас, модель сама останавливается после 5-го эмодзи.
        var (positions, error) = await IntuitionProbeRunner.RunAsync(
            IntuitionPrompts.BuildVibePrompt(Text), nProbs: 20, nPredict: 16, cancellationToken);
        if (positions is null)
        {
            return error!;
        }

        // 1) Последовательность модели: выбранные эмодзи в порядке вывода (ранжирование).
        var sequence = positions
            .Select(p => IntuitionProbeRunner.GeneratedToken(p).Trim())
            .Where(MemoryCategories.IsEmojiToken)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (sequence.Count == 0)
        {
            return "Intuition (vibe): the model produced no single emoji tokens — try a shorter text.";
        }

        // 2) Накопленное распределение по всем эмодзи окон (нормировано в 1, как слои памяти).
        var layers = IntuitionProbeRunner.AccumulateEmoji(
            positions, QwenPlayground.Core.Tokenizers.QwenVocabDecoder.ByteValue);

        var mode = AppSettings.Get().IntuitionFormat;
        var sb = new StringBuilder();
        sb.Append("Intuition (vibe):");
        if (mode == IntuitionFormat.Compact)
        {
            sb.Append(' ').Append(string.Join(" ", sequence));
        }
        else
        {
            foreach (var emoji in sequence)
            {
                sb.Append('\n').Append(emoji);
            }
        }
        if (layers.Count > 0)
        {
            sb.Append('\n').Append(IntuitionFormat.Distribution(
                mode, "Distribution (top 10)",
                layers.OrderByDescending(kv => kv.Value).Take(10)
                    .Select(kv => (Name: kv.Key, Value: kv.Value))));
        }
        else
        {
            sb.Append("\n(window decoding unavailable — emoji sequence only)");
        }
        if (AppSettings.Get().IntuitionDebugDump)
        {
            sb.Append('\n').Append(IntuitionProbeRunner.FormatVibeDebug(
                positions, QwenPlayground.Core.Tokenizers.QwenVocabDecoder.ByteValue));
        }
        return sb.ToString();
    }
}

/// <summary>
/// Оформление вывода тулов «интуиции» (AppSettings.IntuitionFormat):
/// "compact" — всё в одну строку (минимум токенов); "lines" — каждый пункт с новой строки;
/// "lines+bars" — то же + визуальный индикатор размера (10 ячеек: ======----).
/// </summary>
internal static class IntuitionFormat
{
    public const string Compact = "compact";
    public const string Lines = "lines";
    public const string LinesWithBars = "lines+bars";

    /// <summary>
    /// Заголовок + пункты распределения.
    /// compact: "header: a=0,65, b=0,35"
    /// lines: "header:\na=0,65\nb=0,35"
    /// lines+bars: "header:\na=0,65 ======----\nb=0,35 ===-------"
    /// Бар нормирован ОТНОСИТЕЛЬНО максимума в списке (макс = полный бар):
    /// форма распределения видна даже когда все доли малы (vibe: 0,01-0,2).
    /// </summary>
    public static string Distribution(string mode, string header, IEnumerable<(string Name, double Value)> items)
    {
        var list = items.ToList();
        if (mode == Compact || list.Count == 0)
        {
            return $"{header}: {string.Join(", ", list.Select(i => $"{i.Name}={i.Value:0.##}"))}";
        }
        var max = list.Max(i => i.Value);
        var sb = new StringBuilder(header).Append(':');
        foreach (var item in list)
        {
            sb.Append('\n').Append(item.Name).Append('=').Append(item.Value.ToString("0.##"));
            if (mode == LinesWithBars)
            {
                sb.Append(' ').Append(Bar(max > 0 ? item.Value / max : 0.0));
            }
        }
        return sb.ToString();
    }

    /// <summary>10-ячеечный бар: заполнение пропорционально значению (0..1).</summary>
    public static string Bar(double value)
    {
        var filled = Math.Clamp((int)Math.Round(Math.Clamp(value, 0.0, 1.0) * 10), 0, 10);
        return new string('=', filled) + new string('-', 10 - filled);
    }
}
