using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Templates;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Сборка «следующего промпта» main-чата и точный подсчёт его токенов: история +
/// инжектируемая идентичность + рекламируемые инструменты + state-блок — ровно то,
/// что реально уйдёт в /completion (та же сборка, что внутри AgentLoop).
/// Потребители: превью в UI, бюджет-гвард цикла («влезет ли следующий ход»), state-блок.
///
/// Токены — ТОЛЬКО фактические серверные (/tokenize): если сервер не ответил числом,
/// это ошибка, а не повод для оценки chars/4. Конфигурация читается из синглтона настроек.
/// </summary>
public sealed class PromptPipeline
{
    private readonly Func<IReadOnlyList<ChatMessage>> _conversation;
    private readonly Func<string?> _systemPrompt;
    private readonly ToolRegistry _tools;
    private readonly Func<IReadOnlyList<ToolGroup>> _activeShelves;
    private readonly ServerProps _serverProps;
    private readonly Func<IReadOnlyList<ChatMessage>, StateBlock?> _stateBlock;
    private readonly Func<CancellationToken, Task<MultimodalContext?>> _multimodal;
    private readonly Func<string, ICompletionSource> _createSource;

    public PromptPipeline(
        Func<IReadOnlyList<ChatMessage>> conversation,
        Func<string?> systemPrompt,
        ToolRegistry tools,
        ServerProps serverProps,
        Func<IReadOnlyList<ChatMessage>, StateBlock?> stateBlock,
        Func<CancellationToken, Task<MultimodalContext?>> multimodal,
        Func<string, ICompletionSource>? createSource = null,
        Func<IReadOnlyList<ToolGroup>>? activeShelves = null)
    {
        _conversation = conversation;
        // Единый с ходом резолвер системного промпта: идентичность main или цель сессии.
        _systemPrompt = systemPrompt;
        _tools = tools;
        _serverProps = serverProps;
        _stateBlock = stateBlock;
        _multimodal = multimodal;
        // Фабрика источников: по умолчанию llama.cpp-клиент; тесты подставляют заглушку.
        _createSource = createSource ?? (endpoint => new LlmCompletionClient(endpoint));
        _activeShelves = activeShelves ?? (() => Array.Empty<ToolGroup>());
    }

    /// <summary>История + системный промпт (инъекция той же семантики, что SystemPromptInjection в цикле).</summary>
    public List<ChatMessage> BuildRenderMessages()
    {
        var messages = new List<ChatMessage>(_conversation());
        var system = _systemPrompt();
        if (system is not null)
        {
            messages = SystemPromptInjection.Apply(messages, system);
        }
        return messages;
    }

    /// <summary>
    /// Последний ФАКТИЧЕСКИЙ серверный счёт: кэш и скан последнего assistant-хода живут
    /// в <see cref="ServerProps"/> (см. LastActualPromptTokens) — единое хранилище серверных фактов.
    /// </summary>
    public int LastActualPromptTokens() => _serverProps.LastActualPromptTokens(_conversation());

    /// <summary>
    /// Инструменты рекламируются только в агентном режиме (задан корень проекта). Core — всегда;
    /// активные полки докидываются в конец (стабильный порядок: core по имени, затем полки по
    /// enum, внутри полки по имени). Активация полки докидывает хвост — префикс (core) не
    /// сдвигается, его KV-кеш сохраняется; меняется только диалог (неизбежно при смене промпта).
    /// </summary>
    private IReadOnlyList<ToolDefinition>? AdvertisedTools
    {
        get
        {
            if (AppSettings.Get().ProjectRoot.Trim().Length == 0)
            {
                return null;
            }
            var tools = new List<ToolDefinition>(_tools.DefinitionsByGroup(ToolGroup.Core));
            foreach (var group in _activeShelves().OrderBy(g => g))
            {
                tools.AddRange(_tools.DefinitionsByGroup(group));
            }
            // Память выключена — memory_*-тулы не рекламируем (совпадает с реальным запросом).
            return tools.Where(d => MemoryToolGate.ShouldAdvertise(d.Name)).ToList();
        }
    }

    /// <summary>Лёгкий рендер для превью в UI: без state-блока и мультимодальности.</summary>
    public string RenderForPreview()
    {
        var messages = BuildRenderMessages();
        return messages.Count == 0
            ? string.Empty
            : QwenChatTemplate.Render(messages, AdvertisedTools,
                addGenerationPrompt: true, reasoningEffort: AppSettings.Get().ReasoningEffort).Prompt;
    }

    /// <summary>
    /// Рендер следующего промпта 1-в-1 как в AgentLoop (state-блок, мультимодальные маркеры,
    /// генерационный суффикс). Чтобы знать, влезет ли ход в окно, считать надо именно эту строку.
    /// </summary>
    public async Task<string> RenderNextAsync(CancellationToken cancellationToken)
    {
        var messages = BuildRenderMessages();
        if (messages.Count == 0)
        {
            return string.Empty;
        }
        var stateBlock = _stateBlock(messages);
        var multimodal = await _multimodal(cancellationToken);
        return QwenChatTemplate.Render(messages, AdvertisedTools,
            addGenerationPrompt: true, reasoningEffort: AppSettings.Get().ReasoningEffort,
            stateBlock: stateBlock,
            mediaMarker: multimodal?.MediaMarker,
            artifactsProvider: multimodal?.ArtifactsProvider).Prompt;
    }

    /// <summary>
    /// Точные токены следующего промпта по версии СЕРВЕРА (/tokenize, /api/extra/tokencount):
    /// единственный источник правды для бюджета. Без числа — исключение: генерация не начнётся
    /// вслепую. Успешный счёт кэшируется как «последний фактический».
    /// </summary>
    public async Task<int> CountNextTokensAsync(CancellationToken cancellationToken)
    {
        await _serverProps.FetchAsync(AppSettings.Get().Endpoint, cancellationToken);
        var prompt = await RenderNextAsync(cancellationToken);
        if (prompt.Length == 0)
        {
            return 0;
        }
        using var client = _createSource(AppSettings.Get().Endpoint);
        var count = await client.CountTokensAsync(prompt, cancellationToken);
        if (count is not { } exact)
        {
            throw new InvalidOperationException(
                "Сервер не вернул точное количество токенов (/tokenize, /api/extra/tokencount). " +
                "Генерация остановлена: точный размер промпта неизвестен.");
        }
        _serverProps.SetLastPromptTokens(exact);
        return exact;
    }
}
