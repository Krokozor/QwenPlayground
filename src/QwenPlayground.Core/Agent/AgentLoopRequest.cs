using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Runtime;
using QwenPlayground.Core.Templates;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Agent;

/// <summary>
/// Параметры одного хода агентного цикла. Конфигурация (endpoint, семплер, лимиты,
/// проект) НЕ протаскивается параметрами — цикл читает её сам из профиля настроек
/// скоупа (<see cref="Runtime"/>, по умолчанию процессный синглтон) в момент старта
/// хода (pull-модель, паттерн NekoBot). В реквесте остаётся только то, что решает
/// вызывающий: разговор, колбэки-перехватчики, режимные флаги и точечные переопределения.
/// </summary>
public sealed record AgentLoopRequest
{
    /// <summary>
    /// Разговор (мутируется циклом: assistant/tool-сообщения добавляются в него).
    /// ChatLog присваивает стабильные ID при добавлении — отдельный assigner не нужен.
    /// </summary>
    public required ChatLog Conversation { get; init; }

    /// <summary>
    /// Переопределение опций генерации (сервисный профиль с большим потолком токенов и т.п.).
    /// null — собрать из настроек (<c>AppSettings.Get().ToGenerationOptions()</c>).
    /// </summary>
    public GenerationOptions? Generation { get; init; }

    // Жёсткий потолок итераций — последний предохранитель (дизайн от 2026-08-16):
    // самоограничение — через sanity_check-nag, а не обрыв посреди дела.
    /// <summary>Потолок итераций; null — взять из настроек (0 там = без лимита).</summary>
    public int? MaxIterations { get; init; }
    public bool NagOnNoToolCall { get; init; }
    public string? NagText { get; init; }
    public int MaxNags { get; init; } = 2;
    public bool ContinueLastAssistant { get; init; }

    /// <summary>false — single-чат: инструменты не рекламируются и не выполняются.</summary>
    public bool AllowToolExecution { get; init; } = true;

    /// <summary>null — рекламируется полный реестр; пустой список — ничего не рекламировать.</summary>
    public IReadOnlyList<ToolDefinition>? ToolDefinitions { get; init; }

    /// <summary>State-блок (msg_id, время, контекст, nag) — свежий на каждом рендере.</summary>
    public Func<IReadOnlyList<ChatMessage>, StateBlock?>? StateProvider { get; init; }

    /// <summary>Динамический системный промпт (идентичность + слои памяти): подставляется на первое место при каждом рендере, в историю не пишется.</summary>
    public Func<IReadOnlyList<ChatMessage>, string?>? SystemPromptProvider { get; init; }

    /// <summary>Подмена исполнения инструментов (оркестратор перехватывает координационные инструменты).</summary>
    public Func<string, JsonObject, ToolContext, CancellationToken, Task<ToolExecutionResult>>? ToolExecutor { get; init; }

    /// <summary>Итераций без sanity_check, после которых в state-блок ставится наг; 0 — выключено. null — взять из настроек.</summary>
    public int? SanityCheckInterval { get; init; }

    /// <summary>Усилие размышления хода; null — взять из настроек (точка переопределения целью/оркестратором).</summary>
    public ReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>Вызывается перед каждым рендером промпта (внутри может сделать компакцию).</summary>
    public Func<CancellationToken, Task>? ContextBudgetGuard { get; init; }

    /// <summary>Мультимодальность: маркер из /props + провайдер base64 по msgId.</summary>
    public MultimodalContext? Multimodal { get; init; }

    /// <summary>Каталог сессии (sessions/&lt;id&gt;): артефакты сообщений пишутся в папку реальной сессии.</summary>
    public string? SessionDir { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Фабрика источников завершений (по эндпоинту). null — llama.cpp-клиент.
    /// Точка для альтернативных бэкендов и тестовых заглушек; оркестратор может
    /// гонять дочерних агентов на других моделях, не меняя цикл.
    /// </summary>
    public Func<string, ICompletionSource>? CompletionSourceFactory { get; init; }

    /// <summary>
    /// Коллбэк «факт сохранён в память» — прокидывается в ToolContext для memory_add,
    /// чтобы владелец поднял запись в surfaced-пул. null — контексты без памяти.
    /// </summary>
    public Action<MemoryItem>? OnFactSaved { get; init; }

    /// <summary>
    /// Скоуп агента (профиль настроек + маршрут интерактива). null — main-агент:
    /// настройки из процессного синглтона, интерактив — оконный фасад
    /// <see cref="Tools.AgentInteraction"/>. Дочерний агент оркестратора передаст
    /// собственный скоуп, не меняя цикл.
    /// </summary>
    public AgentRuntime? Runtime { get; init; }
}
