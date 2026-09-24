using System.Text;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Probes;

/// <summary>
/// Промпты «интуиции» — логит-пробы СОБСТВЕННОЙ модели (IntuitionPrompts → SelfProbePositionsAsync).
/// Формат — шаблон Qwen (маркеры из QwenSpecialTokens, единый источник) с no-think префиллом
/// (пустой блок размышления — аналог enable_thinking=false в шаблоне): модель отвечает сразу,
/// без thinking-преамбулы, которая сжирает n_predict.
///
/// Три примитива зеркалят пробы памяти (там — компаньон-модель, Gemma-формат), но для
/// своей модели и произвольных вопросов:
///  · choice — multichoice по вариантам (аналог rerank: буквы A..N + None);
///  · rating — ординальная шкала 0-9 одним токен-цифрой (аналог MemorySimilarity.Judge);
///  · vibe — эмодзи-распределение (аналог BuildEmojiPrompt + AccumulateLayers).
/// Парсеры (ParseChoiceLetters/DigitDistribution) — чистые функции, тестируются без сети.
/// </summary>
public static class IntuitionPrompts
{
    /// <summary>
    /// No-think префилл: роль assistant + пустой блок размышления. ВАЖНО: после открывающего
    /// маркера блока — ОДИН \n (как в compile.py, проверено живой пробой): с двумя \n модель
    /// на Qwen3.8-27B-UD считает блок «в процессе» и начинает думать (эмодзи-проба деградировала
    /// в thinking-преамбулу). Шаблон chat-эндпоинта (enable_thinking=false) пишет \n\n — для
    /// сырого /completion работает только одиночный.
    /// </summary>
    private static string NoThinkPrefill() =>
        QwenSpecialTokens.ImStart + QwenSpecialTokens.Assistant + "\n" +
        QwenSpecialTokens.ThinkStart + "\n" +
        QwenSpecialTokens.ThinkEnd + "\n\n";

    /// <summary>Собирает raw-промпт: system + user (формат хода из шаблона) + no-think префилл.</summary>
    private static string Chat(string system, string user) =>
        QwenSpecialTokens.ImStart + QwenSpecialTokens.System + "\n" + system + QwenSpecialTokens.ImEnd + "\n" +
        QwenSpecialTokens.ImStart + QwenSpecialTokens.User + "\n" + user + QwenSpecialTokens.ImEnd + "\n" +
        NoThinkPrefill();

    /// <summary>
    /// Multichoice: варианты A..N + «None of the above» (буква N). Модель отвечает буквами
    /// (в токенизаторе Qwen — одиночные токены, проверено живой пробой); распределение по
    /// буквам = интуиция. optionCount для парсера = варианты + None.
    /// </summary>
    public static string BuildChoicePrompt(string question, IReadOnlyList<string> options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an assistant that answers multiple-choice questions.");
        sb.AppendLine("Choose the option(s) that best answer the question.");
        sb.AppendLine();
        sb.AppendLine($"Question:");
        sb.AppendLine($"\"{question}\"");
        sb.AppendLine();
        sb.AppendLine("Options:");
        for (var i = 0; i < options.Count; i++)
        {
            sb.AppendLine($"{(char)('A' + i)}: {options[i]}");
        }
        var noneLetter = (char)('A' + options.Count);
        sb.AppendLine($"{noneLetter}: None of the above");
        sb.AppendLine();
        sb.AppendLine("Answer with option letters only, one letter at a time, starting with the best.");
        sb.AppendLine("* Do not repeat — each letter must be unique.");
        sb.AppendLine($"* Use ONLY (A-{noneLetter}) chars. Words are NOT allowed.");
        sb.AppendLine($"* Example: B, or only {noneLetter} if nothing fits.");
        return Chat(
            "You are an assistant that answers multiple-choice questions. " +
            "Choose the option(s) that best answer the question.",
            sb.ToString().TrimEnd('\n'));
    }

    /// <summary>
    /// Ординальная шкала 0-9: один токен-цифра (аналог MemorySimilarity.BuildPairPrompt).
    /// Scale задаёт семантику концов шкалы (по умолчанию — да/нет).
    /// </summary>
    public static string BuildRatingPrompt(string statement, string scale = "")
    {
        var scaleText = string.IsNullOrWhiteSpace(scale)
            ? "0 = no / not at all / very low, 9 = yes / to the maximum extent / very high"
            : scale.Trim();
        var user =
            "Rate the following statement on a 0-9 scale.\n" +
            $"{scaleText}\n\n" +
            $"Statement:\n\"{statement}\"\n\n" +
            "Answer with ONE digit (0-9) only. No words, no punctuation.";
        return Chat(
            "You are an assistant that rates statements on an ordinal scale.",
            user);
    }

    /// <summary>
    /// Эмодзи-вайб (аналог MemoryClassifier.BuildEmojiPrompt, Qwen-формат): модель называет
    /// ровно 5 одиночных эмодзи подряд. Ключ к формату — ПРИМЕР в user-сообщении (проверено
    /// живой пробой: без примера Qwen делает нумерованный список «1. 💥 2. 🐛» или
    /// недодекодируемый хвост-токен; с примером — ровно 5 чистых эмодзи, у каждого —
    /// записанное распределение).
    /// </summary>
    public static string BuildVibePrompt(string text)
    {
        var system =
            "Describe the text using SINGLE emoji characters.\n" +
            "Output exactly 5 emoji in a row, one after another, most relevant first.\n" +
            "Output ONLY emoji characters - no numbers, no letters, no punctuation, no newlines.\n" +
            "Use only BASIC emoji - no combined emoji, no emoji with modifiers.\n" +
            "Each emoji must be UNIQUE - don't repeat the same emoji.";
        var user = "Example answer for a happy summer day: 😊☀️🎉🍦😴\n\n" +
                   $"Text: {text}\n\nThe 5 emoji for this text (same format, only emoji, exactly 5):";
        return Chat(system, user);
    }

    /// <summary>
    /// Буквы из argmax-позиций (multichoice): уникальные буквы A..A+optionCount (включая None)
    /// в порядке появления. Аналог MemoryClassifier.ParseRerankLetters для интуиции.
    /// </summary>
    public static IReadOnlyList<int> ParseChoiceLetters(IReadOnlyList<ProbeResult> positions, int optionCount)
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

    /// <summary>
    /// Ординальное распределение по цифрам 0-9 из первой позиции, содержащей цифры
    /// (сырые вероятности top_logprobs, нормировка): взвешенный балл + энтропия (биты) +
    /// Found (цифры нашлись). Ядро MemorySimilarity.Judge без вердиктов/порогов.
    /// Цифр нет — максимально неопределённо (Found=false).
    /// </summary>
    public static (double Score, double Entropy, double[] Distribution, bool Found) DigitDistribution(
        IReadOnlyList<ProbeResult> positions)
    {
        ProbeResult? first = null;
        foreach (var position in positions)
        {
            if (position.TopTokens.Any(t => FirstDigit(t.Token) is not null))
            {
                first = position;
                break;
            }
        }
        if (first is null)
        {
            return (4.5, Math.Log2(10), new double[10], false);
        }

        var digits = new double[10];
        var total = 0.0;
        foreach (var token in first.TopTokens)
        {
            var digit = FirstDigit(token.Token);
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
            return (4.5, Math.Log2(10), new double[10], false);
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
        return (score, entropy, dist, true);
    }

    /// <summary>Первая встречающаяся цифра в токене («9», « 9»); null — цифр нет.</summary>
    public static int? FirstDigit(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }
        foreach (var ch in token)
        {
            if (ch is >= '0' and <= '9')
            {
                return ch - '0';
            }
        }
        return null;
    }
}
