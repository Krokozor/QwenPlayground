using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Probes;

/// <summary>Один токен из окна топ-N логпробов.</summary>
public sealed record ProbeToken(string Token, double LogProb);

/// <summary>
/// Результат пробы — «предответное» состояние модели: распределение по следующему токену.
/// Аргмакс + энтропия = сигнал уверенности, который обычный семплер уничтожает.
/// </summary>
public sealed record ProbeResult(
    string ArgmaxToken,
    double ArgmaxLogProb,
    IReadOnlyList<ProbeToken> TopTokens,
    double Entropy);

/// <summary>
/// Компаньон-модель недоступна (circuit-breaker закрыт после падения пробы). Это НЕ ошибка данных,
/// а «сосед выключен / мёртв» — вызывающий деградирует (память по тексту) без долгого ожидания.
/// </summary>
public sealed class CompanionUnavailableException : Exception
{
    public CompanionUnavailableException(int retryInSeconds)
        : base($"Компаньон-модель временно недоступна (последняя проба упала), повтор через ~{retryInSeconds}с. Фича деградирует: память работает по тексту.")
    {
    }
}

/// <summary>
/// Логит-проба через llama.cpp /v1/chat/completions + logprobs (top_logprobs) и нативный /completion.
/// Задумана для компаньон-модели на ОТДЕЛЬНОЙ машине — не трогает наш KV-кеш, запросы летят
/// параллельно с основной работой. Частота низкая (heartbeat, sanity_check), но HttpClient общий
/// на класс: свой клиент на вызов плодит сокеты в TIME_WAIT. Референс техники: NekoBot
/// LlamaCppMultiTokenProbeRequest.
///
/// ОПЦИОНАЛЬНОСТЬ (сосед может быть выключен):
///  1. Таймаут пробы короткий (10с, не 60) — худший ханг ограничен.
///  2. Circuit-breaker: проба упала (таймаут/соединение) → «недоступно» на cooldown (60с); в
///     течение cooldown дальнейшие пробы fail-fast БЕЗ сети (CompanionUnavailableException),
///     после cooldown одна проба ретраит. Успех сбрасывает. Пользовательская отмена (Stop) НЕ
///     считается недоступностью. Итог: сосед выключен → быстрая деградация, без хангов и без
///     молотка по мёртвому хосту.
/// </summary>
public static class LlmProbeClient
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(ProbeTimeoutSeconds) };

    /// <summary>Таймаут одной пробы (сек). Компаньон — маленькая модель на соседе; 10с с запасом.</summary>
    public const int ProbeTimeoutSeconds = 10;
    /// <summary>Cooldown circuit-breaker (сек): после падения пробы не бьём по соседу столько времени.</summary>
    public const int UnavailableCooldownSeconds = 60;

    private static readonly object _availabilityLock = new();
    private static int _consecutiveFailures;
    private static DateTime _lastFailureUtc = DateTime.MinValue;

    /// <summary>Статус для диагностики: «доступен» / «недоступен, повтор через Nс».</summary>
    public static string AvailabilityStatus
    {
        get
        {
            lock (_availabilityLock)
            {
                if (_consecutiveFailures == 0)
                {
                    return "доступен";
                }
                var elapsed = (DateTime.UtcNow - _lastFailureUtc).TotalSeconds;
                if (elapsed < UnavailableCooldownSeconds)
                {
                    return $"недоступен ({_consecutiveFailures} падений), повтор через ~{(int)(UnavailableCooldownSeconds - elapsed)}с";
                }
                return $"недоступен ({_consecutiveFailures} падений), пробуем снова";
            }
        }
    }

    private static bool IsCircuitOpen()
    {
        lock (_availabilityLock)
        {
            return _consecutiveFailures > 0 &&
                   (DateTime.UtcNow - _lastFailureUtc).TotalSeconds < UnavailableCooldownSeconds;
        }
    }

    private static void RecordSuccess()
    {
        lock (_availabilityLock)
        {
            _consecutiveFailures = 0;
        }
    }

    private static void RecordFailure()
    {
        lock (_availabilityLock)
        {
            _consecutiveFailures++;
            _lastFailureUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Единый чокпойнт опциональности: компаньон-модель не настроена (пустой адрес) → не лезем в
    /// сеть (относительный URL всё равно упал бы), а бросаем ясную ошибку. Все best-effort-вызывающие
    /// (классификация, rerank, memory_add, sanity) ловят и деградируют; память работает по тексту.
    /// </summary>
    private static void EnsureEndpoint(string endpoint)
    {
        if (!AppSettings.Get().CompanionEnabled)
        {
            throw new InvalidOperationException(
                "Компаньон-модель выключена (чекбокс «использовать» в Настройках → Память → Модель для проб). Пробы не летят, память работает по тексту.");
        }
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException(
                "Компаньон-модель не настроена (CompanionEndpoint пуст). Задайте адрес в Настройках → Память → Модель для проб.");
        }
    }

    /// <summary>
    /// HTTP-POST к соседу с circuit-breaker'ом: если «недоступно» (cooldown) — fail-fast без сети;
    /// иначе постим, успех → сброс, сетевое падение/таймаут → фиксируем и пробрасываем. Пользовательская
    /// отмена (cancellationToken) пробрасывается БЕЗ фиксации (это не «сосед мёртв»).
    /// </summary>
    private static async Task<HttpResponseMessage> PostWithBreakerAsync(
        string url, StringContent content, CancellationToken cancellationToken)
    {
        if (IsCircuitOpen())
        {
            var remaining = Math.Max(1,
                (int)(UnavailableCooldownSeconds - (DateTime.UtcNow - _lastFailureUtc).TotalSeconds));
            throw new CompanionUnavailableException(remaining);
        }
        try
        {
            var response = await SharedHttp.PostAsync(url, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            RecordSuccess();
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Stop/отмена — не «недоступность соседа», breaker не трогаем.
        }
        catch (Exception)
        {
            RecordFailure(); // таймаут / соединение / HTTP-ошибка → сосед, вероятно, мёртв.
            throw;
        }
    }

    public static async Task<ProbeResult> ProbeAsync(
        string endpoint, string userPrompt, int nProbs = 20, CancellationToken cancellationToken = default)
    {
        EnsureEndpoint(endpoint);
        var positions = await ProbePositionsAsync(endpoint, userPrompt, nProbs, maxTokens: 1, cancellationToken);
        return positions[0];
    }

    /// <summary>
    /// Мультипозиционная проба: модель генерит до maxTokens токенов (например, последовательность
    /// букв категорий "ABCDE"), на каждой позиции читаем топ-N распределение. Позиции возвращаются
    /// по одной — классификатор накапливает распределение по всему ответу.
    /// </summary>
    public static async Task<IReadOnlyList<ProbeResult>> ProbePositionsAsync(
        string endpoint, string userPrompt, int nProbs = 20, int maxTokens = 8,
        CancellationToken cancellationToken = default)
    {
        EnsureEndpoint(endpoint);
        var payload = new JsonObject
        {
            ["model"] = "probe",
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = userPrompt
            }),
            ["max_tokens"] = Math.Max(1, maxTokens),
            ["temperature"] = 0,
            ["logprobs"] = true,
            ["top_logprobs"] = nProbs
        };

        var response = await PostWithBreakerAsync(
            endpoint.TrimEnd('/') + "/v1/chat/completions",
            new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseProbePositions(body);
    }

    /// <summary>Чистый парсинг ответа — тестируется без сети.</summary>
    public static ProbeResult ParseProbeResponse(string json) => ParseProbePositions(json)[0];

    /// <summary>Парсинг всех позиций ответа: по одному ProbeResult на сгенерированный токен.</summary>
    public static IReadOnlyList<ProbeResult> ParseProbePositions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement.GetProperty("choices")[0]
            .GetProperty("logprobs").GetProperty("content");
        if (content.GetArrayLength() == 0)
        {
            throw new InvalidDataException("probe: empty logprobs in response");
        }

        var positions = new List<ProbeResult>();
        foreach (var position in content.EnumerateArray())
        {
            var tokens = readTopLogprobs(position);
            if (tokens is not null && tokens.Count > 0)
            {
                positions.Add(MakeProbeResult(tokens));
            }
        }

        if (positions.Count == 0)
        {
            throw new InvalidDataException("probe: empty logprobs in response");
        }
        return positions;
    }

    /// <summary>
    /// Нативный end to llama.cpp /completion (не OpenAI-совместимый): промпт — сырая строка в
    /// формате модели (для Gemma — &lt;|turn|&gt;), уверенность читается из n_probs →
    /// completion_probabilities (top_logprobs на каждой позиции). Референс — NekoBot
    /// LlamaCppService.GenerateMultiTokenProbe. Этот эндпоинт возвращает «чистый» ответ
    /// (без thinking-преамбулы, которую chat-шаблон Gemma добавляет в /v1/chat/completions).
    /// </summary>
    public static async Task<IReadOnlyList<ProbeResult>> NativeProbePositionsAsync(
        string endpoint, string prompt, int nProbs = 52, int nPredict = 16,
        string[]? stop = null, CancellationToken cancellationToken = default)
    {
        EnsureEndpoint(endpoint);
        var payload = new JsonObject
        {
            ["prompt"] = prompt,
            ["n_predict"] = Math.Max(1, nPredict),
            ["temperature"] = 0,
            ["top_k"] = 1,
            ["n_probs"] = nProbs
        };
        if (stop is { Length: > 0 })
        {
            payload["stop"] = new JsonArray(stop.Select(s => (JsonNode)s).ToArray());
        }

        var response = await PostWithBreakerAsync(
            endpoint.TrimEnd('/') + "/completion",
            new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseNativeProbePositions(body);
    }

    /// <summary>Парсинг /completion: completion_probabilities → список позиций с топ-N токенами.</summary>
    public static IReadOnlyList<ProbeResult> ParseNativeProbePositions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("completion_probabilities", out var probabilities) ||
            probabilities.GetArrayLength() == 0)
        {
            throw new InvalidDataException("probe: no completion_probabilities in response");
        }

        var positions = new List<ProbeResult>();
        foreach (var position in probabilities.EnumerateArray())
        {
            var tokens = readTopLogprobs(position);
            if (tokens is null || tokens.Count == 0)
            {
                continue;
            }
            positions.Add(MakeProbeResult(tokens));
        }

        if (positions.Count == 0)
        {
            throw new InvalidDataException("probe: no positions with logprobs in response");
        }
        return positions;
    }

    /// <summary>Достаёт top_logprobs из элемента (юнифицировано для chat-completions и /completion).</summary>
    private static List<ProbeToken>? readTopLogprobs(JsonElement container)
    {
        if (!container.TryGetProperty("top_logprobs", out var topLogprobs))
        {
            return null;
        }
        var tokens = new List<ProbeToken>();
        foreach (var lp in topLogprobs.EnumerateArray())
        {
            var token = lp.TryGetProperty("token", out var rawToken)
                ? rawToken.GetString()
                : lp.TryGetProperty("tok_str", out var rawStr) ? rawStr.GetString() : null;
            var logprob = lp.TryGetProperty("logprob", out var rawLogprob)
                ? rawLogprob.GetDouble()
                : lp.TryGetProperty("prob", out var rawProb) ? Math.Log(Math.Max(rawProb.GetDouble(), 1e-12)) : 0;
            if (token is not null)
            {
                tokens.Add(new ProbeToken(token, logprob));
            }
        }
        return tokens;
    }

    /// <summary>ProbeResult из окна топ-N: софтмакс-нормализация для энтропии, argmax = топ-1.</summary>
    private static ProbeResult MakeProbeResult(List<ProbeToken> tokens)
    {
        var maxLogProb = tokens.Max(t => t.LogProb);
        var sum = tokens.Sum(t => Math.Exp(t.LogProb - maxLogProb));
        var entropy = 0.0;
        foreach (var token in tokens)
        {
            var p = Math.Exp(token.LogProb - maxLogProb) / sum;
            if (p > 0)
            {
                entropy -= p * Math.Log(p);
            }
        }

        var argmax = tokens.OrderByDescending(t => t.LogProb).First();
        return new ProbeResult(argmax.Token, argmax.LogProb, tokens, entropy);
    }
}
