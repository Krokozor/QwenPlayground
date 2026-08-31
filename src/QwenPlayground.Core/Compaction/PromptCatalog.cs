using System.Text.Json;
using System.Text.RegularExpressions;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.Compaction;

/// <summary>
/// Каталог промптов суммаризации и слоёв: шаблоны вынесены из кода в config/prompts.json,
/// чтобы владелец мог инспектировать и править их из вкладки «Суммаризация» без пересборки.
/// В шаблонах используются плейсхолдеры «{{key}}»: transcript, l1, l2, l3, temp, max_facts.
/// Отсутствующий/битый файл — встроенные дефолты; поле, которого нет в файле, тоже дефолт.
/// </summary>
public sealed class PromptTemplateSet
{
    public string SummarizationSystem { get; set; } = PromptCatalog.Defaults.SummarizationSystem;
    public string SummarizationUser { get; set; } = PromptCatalog.Defaults.SummarizationUser;
    public string Merge { get; set; } = PromptCatalog.Defaults.Merge;
    public string MergeValidation { get; set; } = PromptCatalog.Defaults.MergeValidation;
    public string SegmentSummary { get; set; } = PromptCatalog.Defaults.SegmentSummary;
    public string SegmentValidation { get; set; } = PromptCatalog.Defaults.SegmentValidation;
    public string MemoryExtraction { get; set; } = PromptCatalog.Defaults.MemoryExtraction;
    public string MemoryExtractionSystem { get; set; } = PromptCatalog.Defaults.MemoryExtractionSystem;

    private static readonly Regex PlaceholderRegex = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Замена плейсхолдеров «{{key}}» на значения; отсутствующий ключ — остаётся как есть.
    /// Замена однопроходная: последовательные Replace подставили бы «{{key}}», встретившийся
    /// ВНУТРИ значения (текст транскрипта модельно-управляемый), содержимым другого аргумента.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> args)
    {
        var text = template ?? string.Empty;
        return PlaceholderRegex.Replace(text, match =>
            args.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value).Trim();
    }
}

public static class PromptCatalog
{
    public static class Defaults
    {
        public const string SummarizationSystem =
            "You are a context compaction assistant. You write dense structured summaries of conversations.";

        // Осторожно: дефолтные шаблоны частично завязаны на тесты (суммар.: verbatim / Open threads /
        // Current state / summary; extraction: правила). Правка дефолтов меняет контракт тестов.
        public const string SummarizationUser =
            "Below is the earlier part of the conversation (it may contain an earlier summary).\n" +
            "Compress it into a dense, structured summary in Russian.\n\n" +
            "Principles:\n" +
            "- Rather keep an extra detail than lose an important one. Exact file paths, type and method names,\n" +
            "  setting values, build and session ids, URLs — verbatim, without paraphrasing.\n" +
            "- If the transcript contains a previous summary — merge it with the new events: refresh what is stale,\n" +
            "  do not repeat what is already described, keep everything still relevant.\n" +
            "- Quote short important user phrasings (rules, agreements) verbatim.\n\n" +
            "Structure:\n" +
            "- The user's goals, preferences and tone\n" +
            "- Decisions made and agreements\n" +
            "- Files created/changed: path — what is inside (briefly)\n" +
            "- Key code and configuration fragments without which work cannot continue\n" +
            "- Errors, incidents and how they were fixed (including workarounds)\n" +
            "- Open threads: questions, TODOs, promises, what is left to do\n" +
            "- Current state: where you stopped, what the next step is\n\n" +
            "Output only the summary, no commentary.\n\n" +
            "{{transcript}}";

        public const string Merge =
            "You are the memory layer merge module of an agent. Below are two layers of long-term memory: " +
            "L1 (oldest) and L2 (middle).\n" +
            "Merge them into one dense layer: keep every still-relevant fact, decision, agreement, " +
            "exact paths, names, ids and settings; update or drop what is stale; do not duplicate. " +
            "The result is a compact coherent text in Russian, no headings or commentary.\n\n" +
            "[L1]\n" +
            "{{l1}}\n\n" +
            "[L2]\n" +
            "{{l2}}";

        public const string MergeValidation =
            "You verify fact loss during memory merging. Below are the old layers L1 and L2 and the result of " +
            "their merge (Temp).\n" +
            "Find important facts that existed in L1/L2 but were lost or distorted in Temp: decisions, " +
            "agreements, exact paths/names/ids/settings, rules, incidents and how they were solved.\n" +
            "Write each fact self-contained in Russian (understandable without context). " +
            "Return only a JSON array of strings, no commentary. If nothing was lost — return an empty array [].\n\n" +
            "[L1]\n" +
            "{{l1}}\n\n" +
            "[L2]\n" +
            "{{l2}}\n\n" +
            "[Temp]\n" +
            "{{temp}}";

        public const string SegmentSummary =
            "You summarize a segment of an agent's conversation. Below is a transcript of the earlier part of the dialogue. " +
            "Compress it into a dense structured summary in Russian — it will become the fresh L3 memory layer.\n\n" +
            "Principles:\n" +
            "- Rather keep an extra detail than lose an important one: exact paths, type and method names, " +
            "setting values, build and session ids, URLs — verbatim.\n" +
            "- Quote decisions, agreements and user rules verbatim or close to the original.\n" +
            "- Errors, incidents and how they were fixed — explicitly.\n" +
            "- Open threads, TODOs and the current state — as separate items.\n\n" +
            "Output only the summary, no commentary or layer headings.\n\n" +
            "{{transcript}}";

        public const string SegmentValidation =
            "You verify fact loss during summarization. Below are the segment transcript and its summary (L3).\n" +
            "Find important facts from the transcript that were lost or distorted in the summary: decisions, " +
            "exact paths/names/ids, agreements, rules, incidents and how they were solved.\n" +
            "Write each fact self-contained in Russian (understandable without context). " +
            "Return only a JSON array of strings, no commentary. If nothing was lost — return an empty array [].\n\n" +
            "[Transcript]\n" +
            "{{transcript}}\n\n" +
            "[L3]\n" +
            "{{l3}}";

        public const string MemoryExtraction =
            "Below is a transcript of the earlier part of the dialogue that was just compacted.\n" +
            "Extract no more than {{max_facts}} long-term facts worth remembering beyond this session:\n" +
            "- decisions made and agreements;\n" +
            "- errors, incidents and how they were fixed;\n" +
            "- user preferences and rules;\n" +
            "- important identifiers (paths, names, ids, settings).\n" +
            "Write each fact self-contained in Russian (understandable without the dialogue context).\n" +
            "Return only a JSON array of strings, no commentary. If nothing is worth saving — return an empty array [].\n\n" +
            "{{transcript}}";

        public const string MemoryExtractionSystem =
            "You are the memory extraction module of an agent. You extract long-term facts as a JSON array " +
            "of strings and return it through the submit_result tool.";
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultConfigPath =>
        Path.Combine(SelfBuildPaths.WorkspaceRoot, "config", "prompts.json");

    /// <summary>Текущий эффективный набор шаблонов (дефолты + переопределения из файла).</summary>
    public static PromptTemplateSet Load(string? path = null)
    {
        var file = path ?? DefaultConfigPath;
        var set = new PromptTemplateSet();
        if (!File.Exists(file))
        {
            return set;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return set;
            }
            Read(root, "SummarizationSystem", v => set.SummarizationSystem = v);
            Read(root, "SummarizationUser", v => set.SummarizationUser = v);
            Read(root, "Merge", v => set.Merge = v);
            Read(root, "MergeValidation", v => set.MergeValidation = v);
            Read(root, "SegmentSummary", v => set.SegmentSummary = v);
            Read(root, "SegmentValidation", v => set.SegmentValidation = v);
            Read(root, "MemoryExtraction", v => set.MemoryExtraction = v);
            Read(root, "MemoryExtractionSystem", v => set.MemoryExtractionSystem = v);
        }
        catch (JsonException)
        {
            // битый файл — возвращаем дефолты; следующий Save их перепишет
        }
        return set;

        static void Read(JsonElement root, string name, Action<string> assign)
        {
            if (root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
                property.GetString() is { } text)
            {
                assign(text);
            }
        }
    }

    public static void Save(PromptTemplateSet set, string? path = null)
    {
        var file = path ?? DefaultConfigPath;
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(set, JsonOptions));
    }

    /// <summary>Встроенный дефолт шаблона по ключу (см. PromptStepItem.Key во вкладке «Суммаризация»).</summary>
    public static string DefaultsOf(string key) => key switch
    {
        "SummarizationSystem" => Defaults.SummarizationSystem,
        "SummarizationUser" => Defaults.SummarizationUser,
        "Merge" => Defaults.Merge,
        "MergeValidation" => Defaults.MergeValidation,
        "SegmentSummary" => Defaults.SegmentSummary,
        "SegmentValidation" => Defaults.SegmentValidation,
        "MemoryExtraction" => Defaults.MemoryExtraction,
        "MemoryExtractionSystem" => Defaults.MemoryExtractionSystem,
        _ => string.Empty
    };
}