using System.Net.Http;
using System.Text.Json;
using QwenPlayground.Core.Chat;

namespace QwenPlayground.Core.Inference;

/// <summary>
/// Кэш свойств llama.cpp-сервера и серверных фактов о токенах:
///  - media_marker — маркер изображений для объектного prompt;
///  - n_ctx — фактическое окно сервера (может быть &lt; настроенного ContextSize; тогда
///    бюджет обязан считать по серверу, иначе промпт не влезет и сервер вернёт 400);
///  - LastPromptTokens — последний ФАКТИЧЕСКИЙ подсчёт промпта (/tokenize бюджета).
/// Кэшируется на TTL: маркер живёт сессию сервера, n_ctx — пока живёт процесс.
/// Отсутствие media_marker — тоже факт (сервер текстовый): успешный ответ кэшируется
/// целиком, иначе текстовый сервер опрашивался бы на каждый вызов.
///
/// LastPromptTokens здесь (а не в PromptPipeline) намеренно: его пишут после успешного
/// подсчёта и читают state-блок с диагностикой — единое хранилище «что сказал сервер»
/// разрывает цикл «конвейер рендерит блок, блок читает подсчёт конвейера».
/// </summary>
public sealed class ServerProps
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    // Общий клиент — как в LlmCompletionClient: свой клиент на каждый запрос плодит сокеты.
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    private string? _mediaMarker;
    private int? _nContext;
    private DateTime _fetchedAtUtc;
    private bool _fetched;

    /// <summary>Маркер изображений (null — сервер текстовый или ещё не опрашивали).</summary>
    public string? MediaMarker => _mediaMarker;

    /// <summary>Реальный n_ctx сервера (null — неизвестен).</summary>
    public int? NContext => _nContext;

    /// <summary>Последний фактический подсчёт промпта сервером; 0 = ещё не отвечал.</summary>
    public int LastPromptTokens { get; private set; }

    public void SetLastPromptTokens(int value)
    {
        if (value > 0)
        {
            LastPromptTokens = value;
        }
    }

    /// <summary>
    /// Последний фактический счёт: свежий кэш /tokenize, затем Generation.PromptTokens
    /// последнего assistant-хода (tokens_evaluated). Оценок chars/4 нет: пока сервер
    /// не ответил хоть что-то — 0 («неизвестно»).
    /// </summary>
    public int LastActualPromptTokens(IReadOnlyList<ChatMessage> conversation)
    {
        if (LastPromptTokens > 0)
        {
            return LastPromptTokens;
        }
        for (var i = conversation.Count - 1; i >= 0; i--)
        {
            if (conversation[i] is { Role: ChatRole.Assistant, Generation.PromptTokens: > 0 } assistant)
            {
                return assistant.Generation.PromptTokens.Value;
            }
        }
        return 0;
    }

    /// <summary>
    /// Запрашивает /props, если кэш протух. Endpoint передаётся на каждый вызов: пользователь
    /// может сменить его, и кэш не должен молчать про смену. Ошибка (сервер недоступен)
    /// глотается и НЕ кэшируется — следующему вызову даём шанс повторить.
    /// </summary>
    public async Task FetchAsync(string endpoint, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (_fetched && now - _fetchedAtUtc < CacheTtl)
        {
            return;
        }
        try
        {
            var props = await SharedHttp.GetStringAsync(endpoint.TrimEnd('/') + "/props", ct);
            using var doc = JsonDocument.Parse(props);
            var root = doc.RootElement;
            if (root.TryGetProperty("media_marker", out var marker) &&
                marker.ValueKind == JsonValueKind.String)
            {
                _mediaMarker = marker.GetString();
            }
            // llama.cpp /props: n_ctx лежит на верхнем уровне или в default_generation_settings.
            if (root.TryGetProperty("n_ctx", out var nTop) && nTop.ValueKind == JsonValueKind.Number)
            {
                _nContext = nTop.GetInt32();
            }
            else if (root.TryGetProperty("default_generation_settings", out var dgs) &&
                     dgs.TryGetProperty("n_ctx", out var nDgs) && nDgs.ValueKind == JsonValueKind.Number)
            {
                _nContext = nDgs.GetInt32();
            }
            _fetched = true;
            // Свежий срез ПОСЛЕ ответа: TTL не укорачивается на время самого запроса.
            _fetchedAtUtc = DateTime.UtcNow;
        }
        catch
        {
            // сервер недоступен — остаёмся на локальных значениях, кэш не помечаем
        }
    }
}
