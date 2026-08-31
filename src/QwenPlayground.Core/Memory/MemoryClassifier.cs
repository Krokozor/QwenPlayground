using System.Text;
using QwenPlayground.Core.Probes;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Memory;

/// <summary>Семантические слои текста: распределения по категориям (A-Z) и эмодзи (вайб).</summary>
public sealed class MemorySemanticLayers
{
    public Dictionary<string, double> Categories { get; set; } = new();
    public Dictionary<string, double> Emoji { get; set; } = new();

    public bool IsEmpty => Categories.Count == 0 && Emoji.Count == 0;

    /// <summary>Строковые представления для index.md: буква и эмодзи с максимальной вероятностью.</summary>
    public string TopCategoryLetter => Categories.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? string.Empty;
    public string TopEmoji => Emoji.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? string.Empty;
    public string TopCategoryName =>
        MemoryCategories.Names.TryGetValue(TopCategoryLetter, out var name) ? name : string.Empty;
}

/// <summary>
/// Классификатор текста на семантические слои через логит-пробы компаньон-модели:
/// нативный llama.cpp /completion (LlmProbeClient.NativeProbePositionsAsync →
/// CompanionEndpoint) с промптом в raw-формате &lt;|turn|&gt; (референс — NekoBot
/// VectorDBPrompts). Нативный эндпоинт возвращает чистый ответ ("A I X") без
/// thinking-преамбулы, которую chat-шаблон Gemma подмешивает в /v1/chat/completions.
/// Промпт просит модель отвечать только буквами/эмодзи, распределение накапливается
/// по всем сгенерированным позициям (модель пишет последовательность "ABCDE").
/// Best-effort: проба упала — возвращаем пустые слои, реколл фолбэчится на текстовый overlap.
/// </summary>
public static class MemoryClassifier
{
    /// <summary>Stop-токены классификации из NekoBot: завершение turn-маркера и перевод строки.</summary>
    public static readonly string[] ClassificationStopTokens = new[] { "<turn|>", "\n" };

    /// <summary>Максимум кандидатов в SecondPass (multichoice) и длина примера факта.</summary>
    public static int RerankMaxCandidates => AppSettings.Get().MemoryRerankMaxCandidates;
    public static int RerankCandidateContentLength => AppSettings.Get().MemoryRerankCandidateContentLength;

    /// <summary>
    /// SecondPass-промпт (аналог reranker'а): кандидаты pass 1 как A/B/C… + опция "None of the above",
    /// модель выбирает релевантные текущему вектору диалога. В raw-формате &lt;|turn&gt; (как в NekoBot
    /// BuildMultichoiceSearchPrompt). Одна и та же модель-классификатор служит и embedding'ом (pass 1),
    /// и reranker'ом (pass 2) — и, вдобавок, сам запрос можно задавать (это и есть вектор диалога).
    /// </summary>
    public static string BuildRerankPrompt(string dialogueContext, IReadOnlyList<MemoryHit> candidates)
    {
        var sb = new StringBuilder();
        sb.Append("<|turn>system\n");
        sb.AppendLine("=== CONTEXTUAL MEMORY SEARCH ===");
        sb.AppendLine("You are an assistant that helps find relevant information from memory.");
        sb.AppendLine("You specialize in retrieving the most relevant memories for the current dialogue.");
        sb.Append("<turn|>\n");
        sb.Append("<|turn>user\n");
        sb.AppendLine("Dialogue context:");
        sb.AppendLine($"\"{dialogueContext}\"");
        sb.AppendLine();
        sb.AppendLine("Memory candidates:");
        var count = Math.Min(candidates.Count, RerankMaxCandidates);
        for (var i = 0; i < count; i++)
        {
            var letter = (char)('A' + i);
            var content = candidates[i].Item.Content;
            if (content.Length > RerankCandidateContentLength)
            {
                content = content[..RerankCandidateContentLength] + "...";
            }
            sb.AppendLine($"{letter}: {content}");
        }
        var noneLetter = (char)('A' + count);
        sb.AppendLine($"{noneLetter}: None of the above (no relevant memories)");
        sb.AppendLine();
        sb.AppendLine("Name the relevant memories one letter at a time, starting with the most relevant.");
        sb.AppendLine("* Do not repeat — each letter must be unique.");
        sb.AppendLine($"* Answer ONLY with related char. Use ONLY (A-{noneLetter}) chars to answer. Words are NOT allowed.");
        sb.AppendLine($"* Example: ABC, or only {noneLetter} if nothing is relevant");
        sb.Append("<turn|>\n");
        sb.Append("<|turn>model\n");
        return sb.ToString();
    }

    /// <summary>
    /// Парсит ответ rerank-пробы: уникальные буквы из argmax-позиций в диапазоне 0..optionCount
    /// (optionCount включает "None"), в порядке появления. None — последний индекс.
    /// </summary>
    public static IReadOnlyList<int> ParseRerankLetters(IReadOnlyList<ProbeResult> positions, int optionCount)
    {
        var selected = new List<int>();
        var seen = new HashSet<int>();
        foreach (var position in positions)
        {
            var token = position.ArgmaxToken.Trim();
            if (token.Length != 1)
            {
                continue;
            }
            var letter = token[0];
            if (letter < 'A' || letter > 'A' + optionCount)
            {
                continue;
            }
            var index = letter - 'A';
            if (seen.Add(index))
            {
                selected.Add(index);
            }
        }
        return selected;
    }

    public static async Task<MemorySemanticLayers> ClassifyAsync(
        string text, string endpoint, int nProbs = 0, CancellationToken cancellationToken = default)
    {
        if (nProbs <= 0) nProbs = AppSettings.Get().MemoryClassifyNProbs;
        var result = await ClassifyDetailedAsync(text, endpoint, nProbs, cancellationToken);
        return result.Layers;
    }

    /// <summary>
    /// Детальная классификация для витрины-валидатора: слои + сырые позиции обеих проб + ошибка.
    /// Валидатор видит не только распределение, но и фактические буквы/эмодзи, что выдала модель,
    /// и причину падения пробы — как в NekoBot, где владелец вручную гонял текст через классификатор.
    /// </summary>
    public static async Task<MemoryClassificationDetailed> ClassifyDetailedAsync(
        string text, string endpoint, int nProbs = 0, CancellationToken cancellationToken = default)
    {
        if (nProbs <= 0) nProbs = AppSettings.Get().MemoryClassifyNProbs;
        var nPredict = AppSettings.Get().MemoryClassifyNPredict;
        var layers = new MemorySemanticLayers();
        IReadOnlyList<ProbeResult>? categoryPositions = null;
        IReadOnlyList<ProbeResult>? emojiPositions = null;
        string? error = null;
        try
        {
            categoryPositions = await LlmProbeClient.NativeProbePositionsAsync(
                endpoint, BuildCategoryPrompt(text), nProbs: nProbs, nPredict: nPredict,
                stop: ClassificationStopTokens, cancellationToken);
            layers.Categories = AccumulateLayers(categoryPositions, MemoryCategories.IsCategoryLetter);

            emojiPositions = await LlmProbeClient.NativeProbePositionsAsync(
                endpoint, BuildEmojiPrompt(text), nProbs: nProbs, nPredict: nPredict,
                stop: ClassificationStopTokens, cancellationToken);
            layers.Emoji = AccumulateLayers(emojiPositions, MemoryCategories.IsEmojiToken);
        }
        catch (OperationCanceledException)
        {
            // Отмена — не «ошибка классификации»: иначе flush после Stop продолжает
            // идти по бюджету с заведомо падающими пробами.
            throw;
        }
        catch (Exception ex)
        {
            // классификация — не критичный путь: реколл без слоёв работает по тексту
            error = ex.Message;
        }
        return new MemoryClassificationDetailed(layers, categoryPositions, emojiPositions, error);
    }

    /// <summary>
    /// Классифицирует текст и наносит слои + строковые Category/Emoji на факт.
    /// Best-effort: проба упала — факт остаётся без слоёв (реколл фолбэчится на текст).
    /// </summary>
    public static async Task EnrichAsync(
        MemoryItem item, string endpoint, CancellationToken cancellationToken = default)
    {
        var layers = await ClassifyAsync(item.Content, endpoint, cancellationToken: cancellationToken);
        if (layers.IsEmpty)
        {
            return;
        }
        item.CategoryLayers = layers.Categories;
        item.EmojiLayers = layers.Emoji;
        item.LayersVersion = CurrentLayerVersion;
    }

    /// <summary>Имя категории по букве-лидеру распределения (пусто, если распределения нет).</summary>
    public static string TopName(IReadOnlyDictionary<string, double> categories)
    {
        var letter = categories.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        return letter is null ? string.Empty : MemoryCategories.Names.GetValueOrDefault(letter, "?");
    }

    /// <summary>Эмодзи-лидер распределения (пусто, если распределения нет).</summary>
    public static string TopEmojiOf(IReadOnlyDictionary<string, double> emoji) =>
        emoji.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// Flush-механизм NekoBot: фоновая (не в горячем потоке чата) вектор-изация воспоминаний.
    /// За каждый проход берёт до budget фактов без слоёв (или со старой LayersVersion — словарь
    /// мог поменяться, модель-классификатор могла смениться) и классифицирует их заново.
    /// Вызывается периодически (heartbeat в App), рано или поздно вся память векторизуется —
    /// и свежие, и старые факты, которым затерли слои. Возвращает число обработанных.
    /// Версия словаря: смена MemoryCategories/модели-проб = bump CurrentLayerVersion.
    /// </summary>
    public const int CurrentLayerVersion = 1;

    /// <summary>
    /// Цели flush: факты без слоёв или со старой версией словаря (включая те, кому слои «затёрли»),
    /// старые первыми. Вынесено отдельно — чистая выборка, тестируется без сети.
    /// </summary>
    public static List<MemoryItem> FlushTargets(MemoryStore store, int budget = 2) =>
        store.List()
            .Where(i => !i.HasSemanticLayers || i.LayersVersion != CurrentLayerVersion)
            .OrderBy(i => i.CreatedAt)
            .Take(budget)
            .ToList();

    public static async Task<int> FlushAsync(
        MemoryStore store, string endpoint, int budget = 2, CancellationToken cancellationToken = default)
    {
        var processed = 0;
        foreach (var item in FlushTargets(store, budget))
        {
            // overwriteStrings: устаревшая версия — старые строковые категории неактуальны.
            await EnrichAsync(item, endpoint, cancellationToken); // словарь мог смениться — строки выводятся из новых распределений
            if (item.HasSemanticLayers)
            {
                store.Update(item);
                processed++;
            }
        }
        return processed;
    }

    /// <summary>
    /// Накопление распределения по позициям пробы: на каждой позиции софтмакс-нормализация окна
    /// топ-N, совпавшие токены добавляют вероятность в слой; итог нормируется на 1.
    /// Ключи токенов тримятся — нативный /completion отдаёт токены с ведущими пробелами (" I").
    /// </summary>
    public static Dictionary<string, double> AccumulateLayers(
        IReadOnlyList<ProbeResult> positions, Func<string, bool> isAllowed)
    {
        var masses = new Dictionary<string, double>();
        foreach (var position in positions)
        {
            var max = position.TopTokens.Max(t => t.LogProb);
            var sum = position.TopTokens.Sum(t => Math.Exp(t.LogProb - max));
            if (sum <= 0)
            {
                continue;
            }
            foreach (var token in position.TopTokens)
            {
                if (!isAllowed(token.Token))
                {
                    continue;
                }
                var p = Math.Exp(token.LogProb - max) / sum;
                var key = token.Token.Trim();
                masses[key] = masses.GetValueOrDefault(key) + p;
            }
        }

        var total = masses.Values.Sum();
        if (total <= 0)
        {
            return new Dictionary<string, double>();
        }
        return masses.ToDictionary(kv => kv.Key, kv => kv.Value / total);
    }

    /// <summary>
    /// Промпт классификации категорий в raw-формате NekoBot: открывающий маркер &lt;|turn&gt; + роль,
    /// закрывающий &lt;turn|&gt; (см. VectorDBPrompts.BuildCategoryPrompt). Подаётся в нативный
    /// llama.cpp /completion — модель отвечает чистой последовательностью букв ("F I S X") без
    /// thinking-преамбулы chat-шаблона (подтверждено живой пробой на Gemma4).
    /// </summary>
    public static string BuildCategoryPrompt(string text)
    {
        var sb = new StringBuilder();
        sb.Append("<|turn>system\n");
        sb.AppendLine("=== CONTEXTUAL MEMORY CLASSIFICATION ===");
        sb.AppendLine("You are an assistant for classifying texts by categories.");
        sb.AppendLine("You specialize in breaking down text into as many categories as possible by its content.");
        sb.Append("<turn|>\n");
        sb.Append("<|turn>user\n");
        sb.AppendLine("Categories (A-Z):");
        foreach (var kvp in MemoryCategories.Names)
        {
            sb.AppendLine($"{kvp.Key}: {kvp.Value}");
        }
        sb.AppendLine();
        sb.AppendLine($"Text:\n{text}");
        sb.AppendLine();
        sb.AppendLine("Name the relevant categories by one letter at a time, starting with the most important.");
        sb.AppendLine("* Do not repeat — each category must be unique.");
        sb.AppendLine("* Answer ONLY with related char to category. Use ONLY (A-Z) chars to answer. Words are NOT allowed.");
        sb.AppendLine("* Example: ABCDE");
        sb.Append("<turn|>\n");
        sb.Append("<|turn>model\n");
        return sb.ToString();
    }

    /// <summary>Промпт эмодзи-вайба в raw-формате NekoBot (VectorDBPrompts.BuildEmojiPrompt).</summary>
    public static string BuildEmojiPrompt(string text)
    {
        var sb = new StringBuilder();
        sb.Append("<|turn>system\n");
        sb.AppendLine("=== DIALOGUE MOOD ANALYSIS ===");
        sb.AppendLine("Describe the text using SINGLE emoji characters.");
        sb.AppendLine("Name ONE emoji at a time, starting with the most relevant.");
        sb.AppendLine("Use only BASIC emoji - no combined emoji, no emoji with modifiers.");
        sb.AppendLine("Each emoji must be UNIQUE - don't repeat the same emoji.");
        sb.Append("<turn|>\n");
        sb.Append("<|turn>user\n");
        sb.AppendLine($"Text: {text}");
        sb.AppendLine();
        sb.AppendLine("Describe with single emoji (one at a time, no combined emoji):");
        sb.Append("<turn|>\n");
        sb.Append("<|turn>model\n");
        return sb.ToString();
    }
}

/// <summary>Результат детальной классификации: слои + сырые позиции проб + ошибка (если была).</summary>
public sealed record MemoryClassificationDetailed(
    MemorySemanticLayers Layers,
    IReadOnlyList<ProbeResult>? CategoryPositions,
    IReadOnlyList<ProbeResult>? EmojiPositions,
    string? Error);

/// <summary>
/// Категории A-Z для классификации памяти. Словарь под Qwen-кодера (в отличие от старого,
/// отзеркаленного под Gemma-«секретаршу»: person/preference/identity/communication убраны,
/// добавлены build/test/debug/refactor/performance/deploy/api/plan/blocker/incident).
/// Смена словаря = bump MemoryClassifier.CurrentLayerVersion: flush-воркер переклассифицирует
/// факты со старой версией слоёв (см. MemoryItem.LayersVersion).
/// </summary>
public static class MemoryCategories
{
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["A"] = "code",
        ["B"] = "build",
        ["C"] = "test",
        ["D"] = "debug",
        ["E"] = "refactor",
        ["F"] = "architecture",
        ["G"] = "tool",
        ["H"] = "project",
        ["I"] = "agent",
        ["J"] = "goal",
        ["K"] = "decision",
        ["L"] = "constraint",
        ["M"] = "performance",
        ["N"] = "data",
        ["O"] = "api",
        ["P"] = "deploy",
        ["Q"] = "owner",
        ["R"] = "report",
        ["S"] = "memory",
        ["T"] = "state",
        ["U"] = "ui",
        ["V"] = "environment",
        ["W"] = "plan",
        ["X"] = "blocker",
        ["Y"] = "incident",
        ["Z"] = "process"
    };

    /// <summary>Одиночная буква A-Z (лидирующие пробелы нативного /completion тримятся), есть в справочнике.</summary>
    public static bool IsCategoryLetter(string token)
    {
        var trimmed = token.Trim();
        return trimmed.Length == 1 && trimmed[0] >= 'A' && trimmed[0] <= 'Z' && Names.ContainsKey(trimmed);
    }

    /// <summary>
    /// Эмодзи-токен по явным диапазонам кодпоинтов (референс — NekoBot
    /// SemanticLayersExtensions.IsEmojiCodePoint). Ручной обход char[] с учётом суррогатных пар:
    /// эмодзи, разбитый токенизатором на два токена, оставляет в каждом токене непарный суррогат,
    /// который не попадает в диапазоны — такой токен игнорируется (как в NekoBot). Модификаторы
    /// (VS16, ZWJ, тон кожи) лежат вне диапазонов — отдельными токенами эмодзи не считаются.
    /// </summary>
    public static bool IsEmojiToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }
        for (var i = 0; i < token.Length; i++)
        {
            var ch = token[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < token.Length && char.IsLowSurrogate(token[i + 1]))
                {
                    if (IsEmojiCodePoint(char.ConvertToUtf32(ch, token[i + 1])))
                    {
                        return true;
                    }
                    i++; // пара обработана
                }
            }
            else if (IsEmojiCodePoint(ch))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Диапазоны эмодзи-кодпоинтов (скопировано из NekoBot).</summary>
    public static bool IsEmojiCodePoint(int codePoint) =>
        codePoint >= 0x1F600 && codePoint <= 0x1F64F || // Emoticons
        codePoint >= 0x1F300 && codePoint <= 0x1F5FF || // Misc symbols & pictographs
        codePoint >= 0x1F680 && codePoint <= 0x1F6FF || // Transport & maps
        codePoint >= 0x1F1E0 && codePoint <= 0x1F1FF || // Flags (regional indicators)
        codePoint >= 0x2600 && codePoint <= 0x26FF ||   // Misc symbols
        codePoint >= 0x2700 && codePoint <= 0x27BF ||   // Dingbats
        codePoint >= 0x1F900 && codePoint <= 0x1F9FF;   // Supplemental symbols
}
