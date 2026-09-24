using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Probes;

/// <summary>Один токен из окна топ-N логпробов. Id — id токена сервера (цел, даже когда строка разорвана).</summary>
public sealed record ProbeToken(string Token, double LogProb, int Id = 0);

/// <summary>
/// Результат пробы — «предответное» состояние модели: распределение по следующему токену.
/// Аргмакс + энтропия = сигнал уверенности, который обычный семплер уничтожает.
/// PositionToken — сгенерированный токен на позиционном уровне («token» в ответе сервера):
/// для мультИБайтовых токенов (эмодзи) в этом билде сервера окно top_logprobs разрывается
/// (U+FFFD), а позиционный токен цел — для них использовать только PositionToken/ArgmaxLogProb.
/// PositionId — id сгенерированного токена (для декодирования через QwenVocabDecoder).
/// </summary>
public sealed record ProbeResult(
    string ArgmaxToken,
    double ArgmaxLogProb,
    IReadOnlyList<ProbeToken> TopTokens,
    double Entropy,
    string? PositionToken = null,
    int PositionId = 0);

/// <summary>
/// Цель пробы недоступна (circuit-breaker закрыт после падения пробы). Это НЕ ошибка данных,
/// а «сосед выключен / мёртв» (или собственный сервер лёг) — вызывающий деградирует
/// (память по тексту) без долгого ожидания.
/// </summary>
public sealed class ProbeUnavailableException : Exception
{
    public ProbeUnavailableException(int retryInSeconds)
        : base($"Цель пробы временно недоступна (последняя проба упала), повтор через ~{retryInSeconds}с. Фича деградирует: память работает по тексту.")
    {
    }
}

/// <summary>
/// Circuit-breaker одной цели пробы (endpoint): падение (таймаут/соединение/HTTP) →
/// «недоступно» на cooldown; в течение cooldown дальнейшие пробы fail-fast БЕЗ сети
/// (CompanionUnavailable → ProbeUnavailableException), после cooldown одна проба ретраит.
/// Успех сбрасывает. Пользовательская отмена (Stop) не считается недоступностью
/// (не фиксируется вызывающим). Пер-эндпоинт: недоступность компаньона и собственной
/// модели не пересекаются. Часы подменяются для тестов.
/// </summary>
public sealed class ProbeBreaker
{
    private readonly int _cooldownSeconds;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private DateTime _lastFailureUtc = DateTime.MinValue;

    public ProbeBreaker(int cooldownSeconds, Func<DateTime>? utcNow = null)
    {
        _cooldownSeconds = cooldownSeconds;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>true — в cooldown (fail-fast). retryInSeconds — сколько осталось ждать.</summary>
    public bool IsOpen(out int retryInSeconds)
    {
        lock (_lock)
        {
            if (_consecutiveFailures == 0)
            {
                retryInSeconds = 0;
                return false;
            }
            var elapsed = (_utcNow() - _lastFailureUtc).TotalSeconds;
            if (elapsed < _cooldownSeconds)
            {
                retryInSeconds = Math.Max(1, (int)(_cooldownSeconds - elapsed));
                return true;
            }
            retryInSeconds = 0;
            return false;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            _lastFailureUtc = _utcNow();
        }
    }

    /// <summary>Статус для диагностики: «доступен» / «недоступен, повтор через Nс».</summary>
    public string Status()
    {
        lock (_lock)
        {
            if (_consecutiveFailures == 0)
            {
                return "доступен";
            }
            var elapsed = (_utcNow() - _lastFailureUtc).TotalSeconds;
            if (elapsed < _cooldownSeconds)
            {
                return $"недоступен ({_consecutiveFailures} падений), повтор через ~{(int)(_cooldownSeconds - elapsed)}с";
            }
            return $"недоступен ({_consecutiveFailures} падений), пробуем снова";
        }
    }
}

/// <summary>
/// Логит-проба через llama.cpp /v1/chat/completions + logprobs (top_logprobs) и нативный /completion.
/// Задумана для компаньон-модели на ОТДЕЛЬНОЙ машине — не трогает наш KV-кеш, запросы летят
/// параллельно с основной работой. Частота низкая (heartbeat, sanity_check), но HttpClient общий
/// на класс: свой клиент на вызов плодит сокеты в TIME_WAIT. Референс техники: NekoBot
/// LlamaCppMultiTokenProbeRequest.
///
/// СВОЯ МОДЕЛЬ (SelfProbePositionsAsync): компаньон недоступен (переезд) → те же пробы
/// (классификация/реколл/rerank/дедуп) гоняются на модели, которая обслуживает чат, но:
///  · пиннинг в сервисный слот (SlotAllocation.Service) — проба не вытесняет KV чата
///    (пиннутый слот не ходит в RAM prompt cache, проба пере-евалюит свой короткий промпт);
///  · только в свободное время (heartbeat/пост-ходовой) — не параллельно, но лучше, чем ничего.
/// Перед собственной пробой сервисный слот erase'ится (best-effort): пиннутый слот от erase
/// ничего не теряет, а это защищает от вырожденного случая одинакового промпта — иначе
/// /completion «продолжил бы» со старого контекста со хвостом прошлого ответа.
///
/// ОПЦИОНАЛЬНОСТЬ (цель может быть выключена):
///  1. Таймаут пробы короткий (10с, не 60) — худший ханг ограничен.
///  2. Circuit-breaker PER-ENDPOINT: проба упала → «недоступно» на cooldown (60с); в течение
///     cooldown дальнейшие пробы fail-fast БЕЗ сети (ProbeUnavailableException), после cooldown
///     одна проба ретраит. Успех сбрасывает. Отмена (Stop) не считается недоступностью.
///     Итог: мёртвая цель → быстрая деградация, без хангов и без молотка по мёртвому хосту;
///     компаньон и своя модель не открывают чужие breaker'ы.
/// </summary>
public static class LlmProbeClient
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(ProbeTimeoutSeconds) };

    /// <summary>Таймаут одной пробы (сек). Компаньон — маленькая модель на соседе; 10с с запасом.</summary>
    public const int ProbeTimeoutSeconds = 10;
    /// <summary>Cooldown circuit-breaker (сек): после падения пробы не бьём по цели столько времени.</summary>
    public const int UnavailableCooldownSeconds = 60;

    private static readonly ConcurrentDictionary<string, ProbeBreaker> _breakers =
        new(StringComparer.OrdinalIgnoreCase);

    private static ProbeBreaker BreakerFor(string endpoint) =>
        _breakers.GetOrAdd(endpoint.TrimEnd('/'), _ => new ProbeBreaker(UnavailableCooldownSeconds));

    /// <summary>Статус цели для диагностики (per-endpoint): «доступен» / «недоступен, повтор через Nс».</summary>
    public static string AvailabilityStatus(string endpoint) => BreakerFor(endpoint).Status();

    /// <summary>
    /// Единый чокпойнт опциональности компаньона: не настроен (пустой адрес) или выключен
    /// (чекбокс) → не лезем в сеть, а бросаем ясную ошибку. Все best-effort-вызывающие
    /// (классификация, rerank, memory_add, sanity) ловят и деградируют; память работает по тексту.
    /// Собственная модель (SelfProbePositionsAsync) этим чекпойнтом не связана — у неё свой.
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
    /// HTTP-POST к цели пробы с circuit-breaker'ом (per-endpoint): если «недоступно» (cooldown) —
    /// fail-fast без сети; иначе постим, успех → сброс, сетевое падение/таймаут → фиксируем и
    /// пробрасываем. Пользовательская отмена (cancellationToken) пробрасывается БЕЗ фиксации
    /// (это не «цель мёртва»).
    /// </summary>
    private static async Task<HttpResponseMessage> PostWithBreakerAsync(
        string url, string endpointKey, StringContent content, CancellationToken cancellationToken)
    {
        var breaker = BreakerFor(endpointKey);
        if (breaker.IsOpen(out var remaining))
        {
            DiagnosticsLog.Log($"probe: fail-fast (circuit open, retry in ~{remaining}s)");
            throw new ProbeUnavailableException(remaining);
        }
        DiagnosticsLog.Log($"probe: POST {url} begin");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await SharedHttp.PostAsync(url, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            breaker.RecordSuccess();
            DiagnosticsLog.Log($"probe: POST {url} done ({sw.ElapsedMilliseconds}ms)");
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiagnosticsLog.Log($"probe: POST {url} canceled ({sw.ElapsedMilliseconds}ms)");
            throw; // Stop/отмена — не «недоступность цели», breaker не трогаем.
        }
        catch (Exception exception)
        {
            breaker.RecordFailure(); // таймаут / соединение / HTTP-ошибка → цель, вероятно, мёртва.
            DiagnosticsLog.Log($"probe: POST {url} FAILED ({sw.ElapsedMilliseconds}ms): {exception.Message}");
            throw;
        }
    }

    public static async Task<ProbeResult> ProbeAsync(
        string endpoint, string userPrompt, int nProbs = 20,
        CancellationToken cancellationToken = default, int? idSlot = null)
    {
        EnsureEndpoint(endpoint);
        var positions = await ProbePositionsAsync(endpoint, userPrompt, nProbs, maxTokens: 1, cancellationToken, idSlot);
        return positions[0];
    }

    /// <summary>
    /// Мультипозиционная проба: модель генерит до maxTokens токенов (например, последовательность
    /// букв категорий "ABCDE"), на каждой позиции читаем топ-N распределение. Позиции возвращаются
    /// по одной — классификатор накапливает распределение по всему ответу. idSlot — пиннинг слота
    /// llama.cpp (null — сервер сам выбирает; для компаньона не нужен, для своей модели обязателен).
    /// </summary>
    public static async Task<IReadOnlyList<ProbeResult>> ProbePositionsAsync(
        string endpoint, string userPrompt, int nProbs = 20, int maxTokens = 8,
        CancellationToken cancellationToken = default, int? idSlot = null)
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
        if (idSlot is { } slot)
        {
            payload["id_slot"] = slot;
        }

        var response = await PostWithBreakerAsync(
            endpoint.TrimEnd('/') + "/v1/chat/completions", endpoint,
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
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
    /// Чистый сборщик payload'а нативного /completion — тестируется без сети.
    /// idSlot (пиннинг) включается только при non-null; stop — только при непустом.
    /// </summary>
    public static JsonObject BuildNativePayload(
        string prompt, int nPredict, int nProbs, string[]? stop = null, int? idSlot = null)
    {
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
        if (idSlot is { } slot)
        {
            payload["id_slot"] = slot;
        }
        return payload;
    }

    /// <summary>
    /// Нативный end to llama.cpp /completion (не OpenAI-совместимый): промпт — сырая строка в
    /// формате модели (для Gemma — &lt;|turn|&gt;), уверенность читается из n_probs →
    /// completion_probabilities (top_logprobs на каждой позиции). Референс — NekoBot
    /// LlamaCppService.GenerateMultiTokenProbe. Этот эндпоинт возвращает «чистый» ответ
    /// (без thinking-преамбулы, которую chat-шаблон Gemma добавляет в /v1/chat/completions).
    ///
    /// ВАЖНО (проверено живой пробой на этом билде сервера): сервер записывает распределение
    /// по КАЖДОМУ сгенерированному токену, КРОМЕ последнего (позиции 0..M-2, M — реально
    /// сгенерировано). n_predict=1 → поле completion_probabilities отсутствует вовсе.
    /// Чтобы получить распределения по первым K токенам ответа — n_predict = K+1.
    /// </summary>
    public static async Task<IReadOnlyList<ProbeResult>> NativeProbePositionsAsync(
        string endpoint, string prompt, int nProbs = 52, int nPredict = 16,
        string[]? stop = null, CancellationToken cancellationToken = default, int? idSlot = null)
    {
        EnsureEndpoint(endpoint);
        return await NativeProbeCoreAsync(endpoint, prompt, nProbs, nPredict, stop, idSlot, cancellationToken);
    }

    /// <summary>
    /// Проба на СОБСТВЕННОЙ модели (AppSettings.Endpoint), закреплённой за сервисным слотом.
    /// «Режим классификатора» на безрыбье компаньона: те же логит-пробы, но на модели чата —
    /// не параллельно с ним, а в свободное время (heartbeat/пост-ходовой), и без вытеснения
    /// KV чата (пиннинг в SlotAllocation.Service). nProbs/nPredict ≤ 0 — дефолты из настроек.
    /// </summary>
    public static async Task<IReadOnlyList<ProbeResult>> SelfProbePositionsAsync(
        string prompt, int nProbs = 0, int nPredict = 0,
        string[]? stop = null, CancellationToken cancellationToken = default)
    {
        var settings = AppSettings.Get();
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new InvalidOperationException(
                "Собственная модель не настроена (Endpoint пуст) — само-проба невозможна.");
        }
        if (nProbs <= 0)
        {
            nProbs = settings.MemoryClassifyNProbs;
        }
        if (nPredict <= 0)
        {
            nPredict = settings.MemoryClassifyNPredict;
        }
        await EraseSlotBestEffortAsync(settings.Endpoint, SlotAllocation.Service, cancellationToken);
        return await NativeProbeCoreAsync(settings.Endpoint, prompt, nProbs, nPredict, stop, SlotAllocation.Service, cancellationToken);
    }

    private static async Task<IReadOnlyList<ProbeResult>> NativeProbeCoreAsync(
        string endpoint, string prompt, int nProbs, int nPredict,
        string[]? stop, int? idSlot, CancellationToken cancellationToken)
    {
        var payload = BuildNativePayload(prompt, nPredict, nProbs, stop, idSlot);
        var response = await PostWithBreakerAsync(
            endpoint.TrimEnd('/') + "/completion", endpoint,
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseNativeProbePositions(body);
    }

    /// <summary>
    /// Best-effort erase слота перед само-пробой (см. SelfProbePositionsAsync): пиннутый слот
    /// не использует RAM prompt cache, так что erase не теряет кеш — он лишь защищает от
    /// «продолжения» со старого контекста при идентичном промпте. Ошибки глотаются: расходящийся
    /// промпт сам сбрасывает слот, проба продолжается.
    /// </summary>
    private static async Task EraseSlotBestEffortAsync(string endpoint, int slotId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SharedHttp.PostAsync(
                $"{endpoint.TrimEnd('/')}/slots/{slotId}?action=erase",
                new StringContent("{}", Encoding.UTF8, "application/json"),
                cancellationToken);
            DiagnosticsLog.Log($"probe: erase slot {slotId} → {(int)response.StatusCode}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Log($"probe: erase slot {slotId} не удался (проба продолжается): {exception.Message}");
        }
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
            var positionToken = position.TryGetProperty("token", out var rawPositionToken)
                ? rawPositionToken.GetString()
                : null;
            var positionId = position.TryGetProperty("id", out var rawPositionId) && rawPositionId.ValueKind == JsonValueKind.Number
                ? rawPositionId.GetInt32()
                : 0;
            positions.Add(MakeProbeResult(tokens, positionToken, positionId));
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
            var id = lp.TryGetProperty("id", out var rawId) && rawId.ValueKind == JsonValueKind.Number
                ? rawId.GetInt32()
                : 0;
            if (token is not null)
            {
                tokens.Add(new ProbeToken(token, logprob, id));
            }
        }
        return tokens;
    }

    /// <summary>ProbeResult из окна топ-N: софтмакс-нормализация для энтропии, argmax = топ-1.</summary>
    private static ProbeResult MakeProbeResult(List<ProbeToken> tokens, string? positionToken = null, int positionId = 0)
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
        return new ProbeResult(argmax.Token, argmax.LogProb, tokens, entropy, positionToken, positionId);
    }
}
