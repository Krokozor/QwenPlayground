using System.Text.Json.Nodes;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Memory;

/// <summary>
/// Извлечение долговременных фактов из сжатого сегмента диалога (упрощённый аналог
/// memories-массива суммаризации NekoBot). Отдельный LLM-вызов после компакции.
/// Текст промпта — из config/prompts.json (см. PromptCatalog), токен «{{max_facts}}».
/// </summary>
public static class MemoryExtractor
{
    public static int MaxFacts => AppSettings.Get().MemoryMaxFactsPerCompaction;

    public static string BuildExtractionPrompt(string transcript)
    {
        var templates = PromptCatalog.Load();
        return PromptTemplateSet.Render(templates.MemoryExtraction, new Dictionary<string, string>
        {
            ["transcript"] = transcript,
            ["max_facts"] = MaxFacts.ToString()
        });
    }

    /// <summary>Допустчивый парсинг ответа модели: JSON-массив строк, возможно в markdown-ограждениях.</summary>
    public static List<string> ParseFacts(string llmOutput)
    {
        var text = (llmOutput ?? string.Empty).Trim();
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return new List<string>();
        }

        try
        {
            if (JsonNode.Parse(text[start..(end + 1)]) is not JsonArray array)
            {
                return new List<string>();
            }
            var facts = new List<string>();
            foreach (var element in array)
            {
                // Только строковые элементы: у чисел/объектов GetValue<string>() бросает InvalidOperationException.
                if (element is JsonValue value && value.TryGetValue<string>(out var fact))
                {
                    var trimmed = fact.Trim();
                    if (trimmed.Length > 0)
                    {
                        facts.Add(trimmed);
                    }
                }
            }
            return facts;
        }
        catch (System.Text.Json.JsonException)
        {
            return new List<string>();
        }
    }
}
