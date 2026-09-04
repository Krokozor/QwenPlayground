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
        // Единый скелет слоя: сегмент-суммаризация (транскрипт → L3) и merge (L1+L2 → L1)
        // пишут ОДИН И ТОТ ЖЕ сорт документа — слои остаются однородными, и merge —
        // структура-сохраняющее слияние. Заголовки секций — на русском, т.к. попадают в
        // вывод; подсказки в квадратных скобках — на английском (инструкция модели).
        private const string LayerTemplate = """
            <template>
            ## Задача
            - [one or two brief sentences: what the user was trying to accomplish; quote verbatim where the exact wording matters, or "(none)"]

            ## Контекст
            - [constraints, agreements, important technical facts and assumptions about the project/environment, or "(none)"]

            ## Решения
            - [decisions and agreements with the reason where known; user rules and directives — verbatim, or "(none)"]

            ## Ошибки и инциденты
            - [error or incident: how it was resolved, plus any related user feedback, or "(none)"]

            ## Состояние
            ### Готово
            - [finished work, verified facts, or changes made; otherwise "(none)"]
            ### В работе
            - [current work, partial changes, or investigation state; otherwise "(none)"]
            ### Заблокировано
            - [blockers, failing commands, or unknowns; otherwise "(none)"]

            ## Открытые нити
            - [explicitly requested work not yet completed, deferred decisions, TODOs, or "(none)"]

            ## Дальше
            1. [the immediate concrete next action, or "(none)"]
            2. [the next action if known, or "(none)"]

            ## Файлы
            - [exact file or directory path: why it matters, or "(none)"]
            </template>
            """;

        private const string LayerRules = """
            Rules:
            - Keep every section, even when empty. Write "(none)" for an empty section — never drop a section.
            - Use terse bullets, not prose paragraphs.
            - Preserve exact file paths, symbols, commands, error strings, URLs, identifiers, and numeric values when known.
            - Capture user feedback and explicit instructions faithfully, especially corrections.
            - Write the layer in Russian. Do not mention the summarization process or that context was compacted. Output only the layer text, no commentary.
            """;

        public const string Merge =
            "You are the memory layer merge module of an agent. Below are two layers of long-term memory: " +
            "<l1> is the generalized history of the past, <l2> is the newer fragment. " +
            "L2 is chronologically later than L1.\n" +
            "Merge them into one dense layer. <l1> and <l2> are discarded after this: anything you do not " +
            "carry into the result is lost.\n\n" +
            "When merging:\n" +
            "- Carry forward objectives, constraints, decisions, agreements, and open threads from <l1> " +
            "even when <l2> does not mention them. Drop only what is finished and no longer needed, or " +
            "too stale to be useful for continuing the work.\n" +
            "- <l2> is more recent than <l1>. Where they conflict, <l2> wins: state the corrected fact and " +
            "drop the old claim.\n" +
            "- Where important changes happened between the eras, describe the delta inline: not only the " +
            "new state, but what changed, from what to what, and why.\n" +
            "- Move completed work from \"В работе\" to \"Готово\". If a blocker has been resolved, drop it " +
            "while keeping any details still needed to continue.\n" +
            "- Update \"Задача\" and \"Дальше\" to reflect the current state.\n" +
            "- The result is a single consolidated layer under the same structure — not a concatenation " +
            "of the two.\n\n" +
            "Output exactly the Markdown structure shown inside <template> and keep the section order " +
            "unchanged. Do not include the <template> tags in your response.\n\n" +
            LayerTemplate + "\n" +
            LayerRules + "\n" +
            "<l1>\n" +
            "{{l1}}\n" +
            "</l1>\n\n" +
            "<l2>\n" +
            "{{l2}}\n" +
            "</l2>";

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
            "You summarize a segment of an agent's conversation. Create a new anchored summary of the " +
            "segment in the <transcript> tags below so the agent can continue the work after the chat is " +
            "compacted. The summary will become the fresh L3 memory layer. " +
            "The <state> blocks in the transcript carry per-message metadata (time, context size, build, " +
            "surfaced memories): use them for chronology, do not summarize them as content.\n\n" +
            "Output exactly the Markdown structure shown inside <template> and keep the section order " +
            "unchanged. Do not include the <template> tags in your response.\n\n" +
            LayerTemplate + "\n" +
            LayerRules + "\n" +
            "<transcript>\n" +
            "{{transcript}}\n" +
            "</transcript>";

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
        "Merge" => Defaults.Merge,
        "MergeValidation" => Defaults.MergeValidation,
        "SegmentSummary" => Defaults.SegmentSummary,
        "SegmentValidation" => Defaults.SegmentValidation,
        "MemoryExtraction" => Defaults.MemoryExtraction,
        "MemoryExtractionSystem" => Defaults.MemoryExtractionSystem,
        _ => string.Empty
    };
}