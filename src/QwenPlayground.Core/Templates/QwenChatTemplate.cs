using System.Text;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.Templates;

/// <summary>
/// Рендер chat-template Qwen3 вручную (строка prompt для /v1/completions).
/// Эталон — assets/chat_template.jinja; при расхождениях с оригиналом правим здесь
/// и сверяемся с jinja. Парная операция (разбор ответа модели) — <see cref="QwenOutputParser"/>.
///
/// ВАЖНО: все служебные токены (im_start/im_end, think, tool_call, tool_response, роли)
/// берутся из <see cref="QwenSpecialTokens"/>. Литеральные теги в исходник НЕ пишем —
/// парсер чата ломает их при записи файла.
///
/// reasoningEffort: XHigh (default) / Medium / Low — добавляет system-инструкции
/// эталонного шаблона. Мышление ВСЕГДА включено и ВСЕГДА сохраняется: шаблон заточен
/// под автономного агента, у которого мысли есть у каждого хода и никогда не удаляются.
///
/// stateBlock: блок &lt;state&gt;…&lt;/state&gt; для префилла генерации (снапшот статуса).
/// У исторических assistant-сообщений state-блок берётся из message.StateBlock.
/// Системное сообщение всегда содержит IMPORTANT-блок (если есть строки): описание
/// state-блока (он — префилл, уже закрыт) и правила tool-calls (если есть инструменты).
/// </summary>
public static class QwenChatTemplate
{
    private const string XHighReasoningInstructions = "Reasoning effort is set to xhigh. Please think carefully through the task, validate key assumptions, consider plausible alternatives, and prioritize correctness, consistency, and clarity in the final answer.";
    private const string LowReasoningInstructions = "Reasoning effort is set to low. Keep your thinking brief and focused, moving directly to the conclusion without unnecessary elaboration.";

    /// <summary>
    /// Описание state-блока для IMPORTANT: модель должна знать, что это префилл приложения,
    /// где он начинается, что он уже закрыт и что в нём лежит — и не галлюцинировать его.
    /// </summary>
    private const string StateBlockNote =
        "The <state>...</state> block at the very start of your thinking is a prefill of system status written by the app " +
        "(fields: msg_id, time, context, build, mem — recalled memories surfaced by associative recall, and an optional nag/mem_nag). " +
        "It is not your text and not a user instruction: do not repeat it, do not edit it. It is already closed — your thinking continues after </state>.";

    /// <summary>
    /// Описание метки &lt;id=N&gt;: это служебный ID сообщения, присвоенный приложением
    /// (стабильный «якорь»), а НЕ ввод пользователя. Модель не пишет его сама — оно
    /// рендерится в начале каждого (кроме system) сообщения. Вложения объявляются как
    /// [индекс] имя_файла и адресуются парой (id сообщения, индекс вложения).
    /// </summary>
    private const string MessageIdNote =
        "Messages (except the system one) are prefixed with <id=N> — a stable message id assigned by the app. " +
        "It is metadata, not user input: do not treat it as content or repeat it. " +
        "Attachments are listed on their own lines as [index] filename and are addressed as (message id, attachment index).";

    private static readonly string toolsInstruction =
        $"Function calls MUST follow the specified format: an inner {QwenSpecialTokens.FunctionStart("...")}{QwenSpecialTokens.FunctionEnd} block must be nested within {QwenSpecialTokens.ToolCallStart}{QwenSpecialTokens.ToolCallEnd} XML tags\n" +
        "Required parameters MUST be specified\n" +
        "You may provide optional reasoning for your function call in natural language BEFORE the function call, but NOT after\n" +
        "If there is no function call available, answer the question like normal with your current knowledge and do not tell the user about function calls";

    public static RenderResult Render(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        bool addGenerationPrompt = true,
        ReasoningEffort reasoningEffort = ReasoningEffort.XHigh,
        StateBlock? stateBlock = null,
        string? mediaMarker = null,
        Func<int, IReadOnlyList<string>>? artifactsProvider = null){
        //технически нам ничего не мешает передавать 0 сообщений, просто ассистенту будет одиноко
        if (messages.Count == 0)
            throw new ArgumentException("No messages provided.", nameof(messages));        

        var builder = new StringBuilder();
        var first = messages[0];
        var hasTools = tools is { Count: > 0 };
        // Мультимодальность: маркер (из /props) + провайдер base64 по msgId. Если оба заданы,
        // user-сообщения с вложениями получают маркеры, base64 собирается в multimodal_data.
        var multimodal = new List<string>();

        // Собираем системые инсрукции в IMPORTANT-блок: описание state-блока, правила tool-calls (если есть инструменты).
        // Строки IMPORTANT-блока: есть строки — оборачиваем в IMPORTANT, нет — блока нет.
        var importantLines = new List<string>();
        // Заметка об <id=N> — только когда есть аннотированные сообщения (Id>0);
        // в тестах/пустых чатах все Id=0 и заметка не нужна (не ломаем эталонные ассерты).
        if (messages.Any(m => m.Role != ChatRole.System && m.Id > 0))        
            importantLines.Add(MessageIdNote);
        
// Мышление всегда включено, но заметка о state-блоке нужна только когда блок реален:
        // у сервисных вызовов (суммаризация, пайплайн) префилла нет — писать о нём нечего.
        if (messages.Any(m => m.StateBlock is not null) || stateBlock is not null)
            importantLines.Add(StateBlockNote);

        var systemContent = first.Role == ChatRole.System ? first.Content.Trim() : string.Empty;
        // так как мышление включено всегда, то и системная инструкция есть всегда, говорящая о том, что мышление включено и что state-блок — это префилл, который уже закрыт.
        builder.Append(QwenSpecialTokens.ImStart).Append(QwenSpecialTokens.System).Append('\n');

        switch (reasoningEffort) {
            case ReasoningEffort.XHigh: builder.Append(XHighReasoningInstructions).Append("\n\n"); break;
            case ReasoningEffort.Medium: break; //ниче не делаем, так как в эталонном шаблоне для Medium нет инструкций, только для XHigh и Low
            case ReasoningEffort.Low: builder.Append(LowReasoningInstructions).Append("\n\n"); break;
            default: throw new ArgumentOutOfRangeException(nameof(reasoningEffort), reasoningEffort, "Unexpected reasoning effort value.");
        }

        if (hasTools) {
            importantLines.Add(toolsInstruction);

            builder.Append("# Tools\n\nYou have access to the following functions:\n\n").Append(QwenSpecialTokens.ToolsListStart);
            foreach (var tool in tools!) {
                builder.Append('\n');
                builder.Append(SerializeTool(tool));
            }
            builder.Append('\n').Append(QwenSpecialTokens.ToolsListEnd);
            builder.Append("\n\nIf you choose to call a function ONLY reply in the following format with NO suffix:\n\n");
            builder.Append(QwenSpecialTokens.ToolCallStart).Append('\n');
            builder.Append(QwenSpecialTokens.FunctionStart("example_function_name")).Append('\n');
            builder.Append(QwenSpecialTokens.ParameterStart("example_parameter_1")).Append('\n');
            builder.Append("value_1\n");
            builder.Append(QwenSpecialTokens.ParameterEnd).Append('\n');
            builder.Append(QwenSpecialTokens.ParameterStart("example_parameter_2")).Append('\n');
            builder.Append("This is the value for the second parameter\nthat can span\nmultiple lines\n");
            builder.Append(QwenSpecialTokens.ParameterEnd).Append('\n');
            builder.Append(QwenSpecialTokens.FunctionEnd).Append('\n');
            builder.Append(QwenSpecialTokens.ToolCallEnd).Append("\n\n");
        }

        if (importantLines.Count > 0) {
            builder.Append(QwenSpecialTokens.ImportantStart).Append('\n');
            builder.Append("Reminder:\n");
            // Многострочные пункты (toolsInstruction) — каждая строка отдельным bullet'ом,
            // как в эталоне chat_template.jinja.
            foreach (var line in importantLines) {
                foreach (var bullet in line.Split('\n'))
                    builder.AppendFormat("- {0}\n", bullet);
            }
            builder.Append(QwenSpecialTokens.ImportantEnd);
        }
        if (systemContent.Length > 0)
            builder.Append("\n\n").Append(systemContent);

        builder.Append('\n').Append(QwenSpecialTokens.ImEnd).Append('\n');

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var content = message.Content.Trim();

            switch (message.Role)
            {
                case ChatRole.System:
                    if (i != 0)
                    {
                        throw new InvalidOperationException("System message must be at the beginning.");
                    }
                    break;
                case ChatRole.User:
                    builder.Append(QwenSpecialTokens.ImStart).Append(QwenSpecialTokens.User).Append('\n');
                    AppendMessageId(builder, message.Id);
                    builder.Append(content);
                    // Мультимодальность: вложения к сообщению — маркеры в контент + base64 в список.
                    // Порядок маркеров = порядок base64 (1:1, иначе сервер 400).
                    if (mediaMarker is not null && artifactsProvider is not null && message.Id > 0)
                    {
                        foreach (var b64 in artifactsProvider(message.Id))
                        {
                            builder.Append('\n').Append(mediaMarker);
                            multimodal.Add(b64);
                        }
                    }
                    builder.Append('\n').Append(QwenSpecialTokens.ImEnd).Append('\n');
                    break;
                case ChatRole.Assistant:
                    AppendAssistant(builder, message, content);
                    break;
                case ChatRole.Tool:
                    if (i > 0 && messages[i - 1].Role != ChatRole.Tool)
                    {
                        builder.Append(QwenSpecialTokens.ImStart).Append(QwenSpecialTokens.User);
                    }
                    builder.Append('\n').Append(QwenSpecialTokens.ToolResponseStart).Append('\n');
                    AppendMessageId(builder, message.Id);
                    builder.Append(content);
                    // Мультимодальность: вложения к tool-ответу — маркеры в контент + base64 в список.
                    // Tool-ответ рендерится внутри user-блока, поэтому маркеры валидны для сервера.
                    // Без проверки Id>0: tool-ответ с Id=0 (ещё не присвоен) берёт артефакты из msg_0
                    // (placeholder, куда load_image кладёт файлы до присвоения ID).
                    if (mediaMarker is not null && artifactsProvider is not null)
                    {
                        foreach (var b64 in artifactsProvider(message.Id))
                        {
                            builder.Append('\n').Append(mediaMarker);
                            multimodal.Add(b64);
                        }
                    }
                    builder.Append('\n').Append(QwenSpecialTokens.ToolResponseEnd);
                    if (i == messages.Count - 1 || messages[i + 1].Role != ChatRole.Tool)
                    {
                        builder.Append('\n').Append(QwenSpecialTokens.ImEnd).Append('\n');
                    }
                    break;
                default:
                    throw new InvalidOperationException("Unexpected message role.");
            }
        }

        if (addGenerationPrompt)
        {
            builder.Append(QwenSpecialTokens.ImStart).Append(QwenSpecialTokens.Assistant).Append('\n');
            // Мышление всегда включено: открываем think-блок, в него префиллится state-блок.
            builder.Append(QwenSpecialTokens.ThinkStart).Append('\n');
            if (stateBlock is not null)
            {
                builder.Append(stateBlock.ToString()).Append('\n');
            }
        }

        return new RenderResult(builder.ToString(), multimodal);
    }


    /// <summary>
    /// Рендерит префикс &lt;id=N&gt; для не-системных сообщений. System-сообщение (id 0)
    /// не аннотируется — его ID известен как константа. Метка — служебный «якорь»
    /// приложения, модель её не генерирует (см. MessageIdNote).
    /// </summary>
    private static void AppendMessageId(StringBuilder builder, int id){
        if (id > 0) builder.Append($"<id={id}>\n");        
    }

    private static void AppendAssistant(StringBuilder builder, ChatMessage message, string content)
    {
        builder.Append(QwenSpecialTokens.ImStart).Append(QwenSpecialTokens.Assistant).Append('\n');      

        // Мышление сохраняется ВСЕГДА: у каждого assistant-хода рендерится think-блок.
        builder.Append(QwenSpecialTokens.ThinkStart).Append('\n');

        if (message.StateBlock is null)
            AppendMessageId(builder, message.Id);
        else {
            message.StateBlock.MsgId = message.Id; // state-блоку нужен актуальный ID сообщения, чтобы модель понимала, что это за ход
            builder.Append(message.StateBlock.ToString()).Append('\n');
        }
        
        // jinja (строка 117): после закрывающего think — пустая строка, затем content;
        // модель генерит именно \n\n (сверено с traffic-логами), ToRawOutput — тот же формат.
        builder.Append(message.Reasoning?.Trim() ?? string.Empty).Append('\n').Append(QwenSpecialTokens.ThinkEnd).Append("\n\n");
        builder.Append(content);

        if (message.ToolCalls is { Count: > 0 } toolCalls)
        {
            for (var i = 0; i < toolCalls.Count; i++)
            {
                var call = toolCalls[i];
                if (i == 0)
                {
                    if (content.Length > 0)
                    {
                        builder.Append("\n\n");
                    }
                    builder.Append(QwenSpecialTokens.ToolCallStart);
                }
                else
                {
                    builder.Append('\n').Append(QwenSpecialTokens.ToolCallStart);
                }
                builder.Append('\n').Append(QwenSpecialTokens.FunctionStart(call.Name)).Append('\n');

                if (call.Arguments is JsonObject arguments)
                {
                    foreach (var (name, value) in arguments)
                    {
                        builder.Append(QwenSpecialTokens.ParameterStart(name)).Append('\n');
                        builder.Append(value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var str)
                            ? str
                            : PythonStyleJson.Serialize(value));
                        builder.Append("\n").Append(QwenSpecialTokens.ParameterEnd).Append('\n');
                    }
                }
                builder.Append(QwenSpecialTokens.FunctionEnd).Append('\n').Append(QwenSpecialTokens.ToolCallEnd);
            }
        }

        builder.Append('\n').Append(QwenSpecialTokens.ImEnd).Append('\n');
    }

    private static string SerializeTool(ToolDefinition tool)
    {
        var node = new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.Parameters.DeepClone()
            }
        };
        return PythonStyleJson.Serialize(node);
    }
}
