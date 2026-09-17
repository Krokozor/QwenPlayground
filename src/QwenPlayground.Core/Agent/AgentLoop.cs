using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Templates;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Agent;

/// <summary>
/// Агентный цикл: рендер промпта → стриминг → парсинг ответа → выполнение tool_calls → повтор.
/// Завершается, когда модель ответила без tool_calls (или после maxNags напоминаний,
/// если включён nagOnNoToolCall), либо по лимиту итераций.
///
/// Ход работы наружу отдаётся потоком <see cref="AgentEvent"/>, поэтому UI и
/// оркестратор могут отображать прогресс, не зная деталей цикла. Выполнение
/// инструментов подменяется через toolExecutor — так оркестратор перехватывает
/// координационные инструменты (say/spawn_agent/...), не трогая сам цикл.
/// </summary>
public sealed class AgentLoop
{
    private readonly ToolRegistry _tools;

    public AgentLoop(ToolRegistry tools)
    {
        _tools = tools;
    }

    public const string DefaultNagText =
        "Continue. If the task is not finished, keep working and use tools. If it is finished, give a brief final answer.";

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentLoopRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Конфигурация читается из профиля скоупа на старте хода (pull-модель):
        // значения фиксируются локально — смена настройки в UI влияет со СЛЕДУЮЩЕГО хода,
        // текущий идёт в стабильном профиле. Переопределения реквеста сильнее настроек.
        var runtime = request.Runtime ?? Runtime.AgentRuntime.Main;
        var settings = runtime.SettingsProvider();
        var generation = request.Generation ?? settings.ToGenerationOptions();
        var maxIterations = request.MaxIterations ?? settings.MaxIterations;
        var sanityCheckInterval = request.SanityCheckInterval ?? settings.SanityCheckInterval;
        var reasoningEffort = request.ReasoningEffort ?? settings.ReasoningEffort;

        // Локали повторяют прежнюю сигнатуру: тело цикла не менялось при переходе
        // на параметр-объект (AgentLoopRequest) — только точка входа.
        // Отмена приходит двумя путями (поле запроса и WithCancellation у await foreach) —
        // объединяем в один токен.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, request.CancellationToken);
        var conversation = request.Conversation;
        var endpoint = settings.Endpoint;
        var projectRoot = settings.ProjectRoot;
        var nagOnNoToolCall = request.NagOnNoToolCall;
        var nagText = request.NagText;
        var maxNags = request.MaxNags;
        var continueLastAssistant = request.ContinueLastAssistant;
        var allowToolExecution = request.AllowToolExecution;
        var toolDefinitions = request.ToolDefinitions;
        var stateProvider = request.StateProvider;
        var systemPromptProvider = request.SystemPromptProvider;
        var toolExecutor = request.ToolExecutor;
        var contextBudgetGuard = request.ContextBudgetGuard;
        var multimodal = request.Multimodal;
        var sessionDir = request.SessionDir;
        cancellationToken = linkedCts.Token;

        using var client = request.CompletionSourceFactory?.Invoke(endpoint) ?? new LlmCompletionClient(endpoint);
        var nags = 0;
        var iterationsSinceSanity = 0;
        var definitions = toolDefinitions ?? _tools.Definitions;
        IReadOnlyList<string>? multimodalData = null;

        var bound = maxIterations > 0 ? maxIterations : int.MaxValue;
        DiagnosticsLog.Log($"AgentLoop: begin (maxIterations={maxIterations}, endpoint={endpoint})");
        for (var iteration = 0; iteration < bound; iteration++)
        {
            DiagnosticsLog.Log($"AgentLoop: iteration {iteration + 1} begin");
            // Бюджет контекста: результат инструментов предыдущей итерации (иногда огромный —
            // файлы, картинки) уже лежит в conversation. Следующий рендер будет больше последнего
            // запроса, поэтому проверяем/сжимаем ДО рендера — иначе переполнение окна ударит
            // при отправке следующего сообщения (сервер вернёт 400).
            if (contextBudgetGuard is not null)
            {
                DiagnosticsLog.Log($"AgentLoop: iteration {iteration + 1}: context budget guard begin");
                await contextBudgetGuard(cancellationToken);
                DiagnosticsLog.Log($"AgentLoop: iteration {iteration + 1}: context budget guard done");
            }

            string prompt;
            ChatMessage? continued = null;
            // State-блок (msg_id, время, контекст, сборка, nag) — свежий на каждом рендере.
            // Вставляется в начало мысли как префилл; ниже пришивается к сообщению,
            // так что блок перистируется с сообщением — модель видит эволюцию статуса.
            var stateBlock = stateProvider?.Invoke(conversation);
            // Sanity-check nag: много итераций без самопроверки — напомнить полем внутри state-блока.
            // Счётчик сбрасывается вызовом инструмента sanity_check (ниже).
            if (sanityCheckInterval > 0 && iterationsSinceSanity >= sanityCheckInterval)
            {
                stateBlock = StateBlock.WithNag(stateBlock,
                    $"{iterationsSinceSanity} iterations without a self-check. Call sanity_check: what are you doing, is there progress, should you change strategy?");
            }
            iterationsSinceSanity++;
            // Копия для рендера (ChatLog здесь читается, не мутируется); инъекция системы — как раньше.
            var messages = new List<ChatMessage>(conversation);
            if (systemPromptProvider is not null)
            {
                var systemContent = systemPromptProvider(conversation);
                if (systemContent is not null)
                {
                    messages = SystemPromptInjection.Apply(messages, systemContent);
                }
            }
            if (iteration == 0 && continueLastAssistant &&
                conversation.Count > 0 && conversation[^1].Role == ChatRole.Assistant)
            {
                continued = conversation[^1];
                // Блок собран с MsgId = «следующий» ID (счётчик), но сообщение-продолжение
                // уже имеет СВОЙ стабильный ID — подменяем, иначе модель видит в префилле
                // чужой msg_id (и персистится блок с ним). Рендер истории синхронизирует
                // MsgId повторно (AppendAssistant) — это страховка для старых записей.
                if (stateBlock is not null)
                {
                    stateBlock.MsgId = continued.Id;
                }
                prompt = QwenChatTemplate.Render(
                             messages.GetRange(0, messages.Count - 1),
                             definitions, addGenerationPrompt: true, reasoningEffort: reasoningEffort, stateBlock: stateBlock)
                         .Prompt + continued.ToRawOutput();
            }
            else
            {
                var renderResult = QwenChatTemplate.Render(messages, definitions,
                    addGenerationPrompt: true, reasoningEffort: reasoningEffort, stateBlock: stateBlock,
                    mediaMarker: multimodal?.MediaMarker, artifactsProvider: multimodal?.ArtifactsProvider);
                prompt = renderResult.Prompt;
                multimodalData = renderResult.MultimodalData;
            }

            var raw = new StringBuilder(continued?.ToRawOutput() ?? string.Empty);
            DiagnosticsLog.Log($"AgentLoop: iteration {iteration + 1}: render done ({prompt.Length} chars), stream begin");
            var streamStart = System.Diagnostics.Stopwatch.StartNew();
            var chunkCount = 0;
            await foreach (var chunk in client.StreamAsync(prompt, generation, multimodalData, cancellationToken))
            {
                raw.Append(chunk);
                chunkCount++;
                yield return new TokenEvent(chunk);
            }
            DiagnosticsLog.Log($"AgentLoop: iteration {iteration + 1}: stream done ({raw.Length} chars, {chunkCount} chunks, {streamStart.ElapsedMilliseconds}ms)");

            var message = continued ?? QwenOutputParser.ParseAssistant(raw.ToString());
            // Префилл не входит в ответ модели — снапшот пришиваем явно: блок, который
            // модель видела в начале мысли, тот же сохраняется с сообщением.
            if (stateBlock is not null)
            {
                message.StateBlock = stateBlock;
            }
            TrafficLog.Log(prompt, raw.ToString());
            if (continued is not null)
            {
                var reparsed = QwenOutputParser.ParseAssistant(raw.ToString());
                continued.Reasoning = reparsed.Reasoning;
                continued.Content = reparsed.Content;
                continued.ToolCalls = reparsed.ToolCalls;
                continued.ThinkingClosed = reparsed.ThinkingClosed;
            }
            else
            {
                // ChatLog.Add присваивает стабильный ID сразу: финализация инструментов и
                // рендеры следующей итерации видят его без отложенной нумерации.
                conversation.Add(message);
            }
            // Токены промпта: ТОЛЬКО фактические значения сервера. Сначала usage ответа
            // (tokens_evaluated — сколько реально ушло в модель); если usage не пришёл —
            // запрашиваем точное количество у сервера (/tokenize). Если и он молчит —
            // остаёмся с null («неизвестно», UI показывает ?), никаких оценок chars/4.
            var promptTokens = client.LastUsage?.PromptTokens
                               ?? await client.CountTokensAsync(prompt, cancellationToken);
            message.Generation = new GenerationInfo
            {
                Prompt = prompt,
                RawOutput = raw.ToString(),
                PromptTokens = promptTokens,
                CompletionTokens = client.LastUsage?.CompletionTokens
            };
            yield return new AssistantMessageEvent(message);

            if (message.ToolCalls is not { Count: > 0 } toolCalls)
            {
                if (nagOnNoToolCall && nags < maxNags)
                {
                    nags++;
                    DiagnosticsLog.Log($"AgentLoop: iteration {iteration + 1}: no tool calls, nag {nags}/{maxNags}");
                    var nag = ChatMessage.User(nagText ?? DefaultNagText);
                    conversation.Add(nag);
                    yield return new NagEvent(nag.Content);
                    continue;
                }
                DiagnosticsLog.Log($"AgentLoop: done (no tool calls, {iteration + 1} iterations)");
                yield return new AgentDoneEvent();
                yield break;
            }

            // Режим без инструментов (single-чат): разметка tool_call в выводе — шум
            // модели, инструменты ей не рекламировались. Сообщение уже в истории, завершаемся.
            if (!allowToolExecution)
            {
                yield return new AgentDoneEvent();
                yield break;
            }

            // Контекст инструментов: стандартные поля + доступ к сообщениям разговора по ID
            // (для message_edit_content и т.п. — мета-данные сообщений). conversation —
            // стабильная ссылка, доступы всегда видят актуальное состояние.
            // Взаимодействие с пользователем инструменты тянут из скоупа контекста
            // (у main это оконный фасад AgentInteraction).
            ChatMessage? FindById(int id) =>
                conversation.FirstOrDefault(m => m.Id == id && m.Role != ChatRole.System);
            var toolContext = new ToolContext(projectRoot,
                FindById,
                (id, content) =>
                {
                    var m = FindById(id);
                    if (m is null)
                    {
                        return false;
                    }
                    m.Content = content;
                    return true;
                },
                sessionDir,
                conversation,
                request.OnFactSaved,
                runtime,
                QwenPlayground.Core.Settings.AppSettings.Get().AdditionalWorkspaces);
            foreach (var call in toolCalls)
            {
                var arguments = call.Arguments as JsonObject ?? new JsonObject();
                DiagnosticsLog.Log($"AgentLoop: iteration {iteration + 1}: tool call '{call.Name}' begin");
                var toolStart = System.Diagnostics.Stopwatch.StartNew();
                yield return new ToolCallStartedEvent(call.Name, arguments);
                var execution = toolExecutor is not null
                    ? await toolExecutor(call.Name, arguments, toolContext, cancellationToken)
                    : await _tools.ExecuteDetailedAsync(call.Name, arguments, toolContext, cancellationToken);
                DiagnosticsLog.Log($"AgentLoop: iteration {iteration + 1}: tool call '{call.Name}' done ({toolStart.ElapsedMilliseconds}ms, result {execution.Text.Length} chars)");
                var toolMessage = ChatMessage.Tool(execution.Text);
                conversation.Add(toolMessage);
                // Финализация: результат уже добавлен в разговор и получил стабильный ID —
                // инструмент «привязывает» себя к своему сообщению (артефакты в msg_<id> и т.п.).
                // Раньше это был костыль: load_image клал файлы в placeholder msg_0, а мы
                // переносили их сюда. Теперь инструмент сам знает свой чат и ID сообщения.
                if (execution.Tool is not null)
                {
                    await execution.Tool.FinalizeAsync(toolContext, toolMessage.Id, cancellationToken);
                }
                // Автокаппинг: большой tool-вывод → полный в attachments/ сообщения, в
                // сообщении остаётся превью + <attachment>. Контекст не раздувается, данные
                // не теряются (read_file чтобы увидеть весь вывод). read_file с явным
                // offset/limit — осознанный запрос, не каппим (модель знает размер).
                CapToolOutput(toolMessage, execution.Text, sessionDir, call.Name, arguments);
                yield return new ToolCallFinishedEvent(call.Name, toolMessage.Content, toolMessage);

                if (call.Name == "sanity_check")
                {
                    iterationsSinceSanity = 0;
                }

                if (File.Exists(SelfBuildPaths.RestartRequestFile))
                {
                    yield return new RestartPendingEvent();
                    yield break;
                }
            }
        }

        // Добрались сюда только если лимит итераций был задан и исчерпан (бесконечный цикл
        // завершается только через yield break выше).
        if (maxIterations > 0)
        {
            yield return new AgentErrorEvent($"reached iteration limit ({maxIterations})");
        }
    }

    /// <summary>
    /// Автокаппинг tool-вывода: если текст больше порога (8 КБ), полный вывод сохраняется в
    /// attachments/ папки артефактов сообщения, а в сообщении остаётся превью (~2 КБ, по
    /// границе строки) + тег &lt;attachment path&gt;. Контекст не раздувается, данные не
    /// теряются (read_file чтобы увидеть весь вывод). Никогда не бросает: сбой каппинга не
    /// ломает ход — вывод остаётся как есть.
    /// </summary>
    private static void CapToolOutput(ChatMessage toolMessage, string text, string? sessionDir, string toolName, JsonObject? arguments)
    {
        // read_file с явным offset/limit — осознанный запрос (модель знает размер), не каппим.
        // Без offset/limit (весь файл) — каппим (safety net). Прочие тулы — каппим.
        var hasOffset = arguments?.TryGetPropertyValue("offset", out _) ?? false;
        var hasLimit = arguments?.TryGetPropertyValue("limit", out _) ?? false;
        if (toolName == "read_file" && (hasOffset || hasLimit))
        {
            return;
        }
        const int threshold = 8 * 1024;
        if (text.Length <= threshold || string.IsNullOrWhiteSpace(sessionDir))
        {
            return;
        }
        try
        {
            var store = new MessageMetaStore(sessionDir);
            var full = store.AddTextArtifact(toolMessage.Id, "output.txt", text);
            // Путь относительно корня workspace (read_file ожидает такой).
            var wsRoot = SelfBuildPaths.WorkspaceRoot;
            var rel = full.StartsWith(wsRoot, StringComparison.OrdinalIgnoreCase)
                ? full[wsRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : full;
            const int preview = 2 * 1024;
            var cut = Math.Min(preview, text.Length);
            var lastNewline = text.LastIndexOf('\n', cut);
            if (lastNewline > preview / 2)
            {
                cut = lastNewline;
            }
            var lines = text.Split('\n').Length;
            toolMessage.Content = text[..cut]
                + $"\n\n[Output truncated — full ({text.Length / 1024}KB, {lines} lines) attached.]"
                + $"\n<attachment path=\"{rel}\">";
            DiagnosticsLog.Log($"AgentLoop: tool output capped (msg {toolMessage.Id}, {text.Length} chars → {rel})");
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Log($"AgentLoop: tool output capping failed (msg {toolMessage.Id}): {exception.Message}");
        }
    }
}
