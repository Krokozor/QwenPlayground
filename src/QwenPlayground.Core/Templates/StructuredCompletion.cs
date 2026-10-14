using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Crash;

namespace QwenPlayground.Core.Templates;

/// <summary>
/// Сервисные LLM-операции (суммаризация, merge слоёв, валидации, извлечение фактов) отдают
/// результат НЕ свободным текстом, а вызовом одного инструмента <see cref="ToolName"/>.
/// Это отменяет хрупкий парсинг по маркерам « response» / срезам «[…]»: ответ структурно
/// валиден в той же схеме, что уже разбирается для агентных tool-вызовов.
/// </summary>
public static class StructuredCompletion
{
    public const string ToolName = "submit_result";
    public const string ResultParam = "result";

    private const string System =
        "You are an internal processing module of an agent. You produce an exactly specified output " +
        "and return it by calling the submit_result tool.";

    public static readonly ToolDefinition FinishTool = new()
    {
        Name = ToolName,
        Description =
            "Return the result of the requested operation. Put the ENTIRE result — nothing more, nothing less " +
            "— into the 'result' parameter. Do not write any answer text outside the tool call.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [ResultParam] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The full result of the operation."
                }
            },
            ["required"] = new JsonArray { ResultParam }
        }
    };

    /// <summary>
    /// Prefill в think-блок: модель «уже начала» мысль с намерением вызвать инструмент.
    /// Страховка для низких квантов (особенно кеша): без префилла модель может забыть
    /// инструкцию «вызови submit_result» и выдать результат текстом — а то и вовсе
    /// не выдать (баг-репорт владельца: суммаризация не всегда проходит корректно).
    /// </summary>
    private const string Prefill =
        "I am working out the result. Then I will call the submit_result tool with the complete result.";

    /// <summary>Рендер промпта: system + user + единственный инструмент submit_result + prefill.</summary>
    public static string Render(string userContent, string? system = null)
    {
        var instruction =
            "\n\nSubmit your result by calling the submit_result tool. Put the ENTIRE result into its 'result' " +
            "parameter. Do not write any answer text outside the tool call.";
        var request = new List<ChatMessage>
        {
            ChatMessage.System(system ?? System),
            ChatMessage.User(userContent.TrimEnd() + instruction)
        };
        // ReasoningEffort.Medium намеренно: в эталонном шаблоне для него НЕТ инструкции («думай больше/меньше»),
        // модель опирается на тренировочное поведение — меньше ошибок, чем на low, меньше разгона, чем на xhigh.
        return QwenChatTemplate.Render(request, tools: [FinishTool], addGenerationPrompt: true,
            reasoningEffort: ReasoningEffort.Medium, prefill: Prefill).Prompt;
    }

    /// <summary>
    /// Достаёт значение параметра result из tool-вызова submit_result; null — не вызван/пуст.
    /// Фолбэк: модель не вызвала инструмент (низкий квант), но записала результат свободным
    /// текстом ПОСЛЕ закрытого think-блока — принимаем его (лучше, чем null, который
    /// прерывает компакцию). Незакрытый think = обрыв генерации, не ответ — null.
    /// </summary>
    public static string? ExtractResult(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        var parsed = QwenOutputParser.ParseAssistant(raw);
        var call = parsed.ToolCalls?.FirstOrDefault(c => c.Name == ToolName);
        if (call?.Arguments is JsonObject arguments &&
            arguments[ResultParam] is JsonValue value)
        {
            try
            {
                var result = value.GetValue<string>();
                return string.IsNullOrWhiteSpace(result) ? null : result;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
        if (parsed.ThinkingClosed && !string.IsNullOrWhiteSpace(parsed.Content))
        {
            DiagnosticsLog.Log("StructuredCompletion: submit_result not called — using free-text answer as result (fallback, low-quant behavior)");
            return parsed.Content.Trim();
        }
        return null;
    }
}