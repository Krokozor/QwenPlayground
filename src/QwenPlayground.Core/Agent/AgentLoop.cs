using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.SelfBuild;
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
        for (var iteration = 0; iteration < bound; iteration++)
        {
            // Бюджет контекста: результат инструментов предыдущей итерации (иногда огромный —
            // файлы, картинки) уже лежит в conversation. Следующий рендер будет больше последнего
            // запроса, поэтому проверяем/сжимаем ДО рендера — иначе переполнение окна ударит
            // при отправке следующего сообщения (сервер вернёт 400).
            if (contextBudgetGuard is not null)
            {
                await contextBudgetGuard(cancellationToken);
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

            await foreach (var chunk in client.StreamAsync(prompt, generation, multimodalData, cancellationToken))
            {
                raw.Append(chunk);
                yield return new TokenEvent(chunk);
            }

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
                    var nag = ChatMessage.User(nagText ?? DefaultNagText);
                    conversation.Add(nag);
                    yield return new NagEvent(nag.Content);
                    continue;
                }
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
                runtime);
            foreach (var call in toolCalls)
            {
                var arguments = call.Arguments as JsonObject ?? new JsonObject();
                yield return new ToolCallStartedEvent(call.Name, arguments);
                var execution = toolExecutor is not null
                    ? await toolExecutor(call.Name, arguments, toolContext, cancellationToken)
                    : await _tools.ExecuteDetailedAsync(call.Name, arguments, toolContext, cancellationToken);
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
                yield return new ToolCallFinishedEvent(call.Name, execution.Text, toolMessage);

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
}
