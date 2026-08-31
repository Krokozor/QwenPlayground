using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QwenPlayground.Core.Inference;

/// <summary>
/// Клиент к llama.cpp-подобному серверу: нативный /completion (legacy) + /tokenize.
/// Нативный эндпоинт выбран сознательно: только он принимает объектный prompt
/// {prompt_string, multimodal_data} — фундамент мультимодальности (см. QwenMultimodalTest).
/// Ответ: content + tokens_evaluated/tokens_predicted; стриминг — SSE-стиль (data: {...}).
///
/// HttpClient общий на весь процесс (<see cref="SharedHttp"/>) — свой клиент на
/// каждый запуск агента плодит сокеты, а в оркестраторе запусков много.
/// BaseAddress у общего клиента выставить нельзя, поэтому адрес собирается из
/// _baseAddress при каждом запросе. Свой HttpMessageHandler можно передать
/// в тестах — тогда клиент owned и Dispose его закрывает.
///
/// TODO(развитие): вынести за интерфейс IInferenceBackend, когда появится второй
/// провайдер (OpenAI-compatible chat API и т.п.). См. refactoring.md, backlog.
/// </summary>
public sealed class LlmCompletionClient : ICompletionSource
{
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseAddress;

    public TokenUsage? LastUsage { get; private set; }

    public LlmCompletionClient(string baseAddress, HttpMessageHandler? handler = null)
    {
        _baseAddress = baseAddress.TrimEnd('/');
        if (handler is null)
        {
            _http = SharedHttp;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
    }

    private string Url(string path) => $"{_baseAddress}/{path}";

    public async Task<CompletionResult> CompleteAsync(string prompt, GenerationOptions options, CancellationToken cancellationToken = default)
    {
        // Usage от предыдущего запроса не должен пережить текущий: при падении здесь
        // наружу торчали бы чужие токены (AgentLoop читает LastUsage после каждого вызова).
        LastUsage = null;
        using var content = BuildContent(prompt, options, stream: false);
        // Нативный /completion (legacy): ответ — {content, tokens_evaluated, tokens_predicted, ...}.
        using var response = await _http.PostAsync(Url("completion"), content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var text = root.TryGetProperty("content", out var contentElement)
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;
        LastUsage = ParseNativeUsage(root);
        return new CompletionResult(text, LastUsage);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string prompt,
        GenerationOptions options,
        IReadOnlyList<string>? multimodalData = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastUsage = null;
        // Нативный /completion (legacy) стримит в SSE-стиле: каждая строка «data: {...}»,
        // чанки разделены пустой строкой. Поля чанка: content, tokens_evaluated,
        // tokens_predicted, stop (true на последнем). Формат подтверждён живым пробом
        // (QwenMultimodalTest/probe_stream_format.py).
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("completion"))
        {
            Content = BuildContent(prompt, options, stream: true, multimodalData)
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }
            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
            {
                continue;
            }
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                // Обрыв строки/мусор от прокси не должны убивать генерацию посреди стрима:
                // пропускаем битый чанк, продолжаем читать поток.
                continue;
            }
            using (document)
            {
                var root = document.RootElement;
                var usage = ParseNativeUsage(root);
                if (usage is not null)
                {
                    LastUsage = usage;
                }
                if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return text;
                    }
                }
                if (root.TryGetProperty("stop", out var stop) && stop.ValueKind == JsonValueKind.True)
                {
                    yield break;
                }
            }
        }
    }

    public async Task<int?> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            // add_special: false — считаем ровно то, что уйдёт в /completion (сырой рендер
            // промпта уже содержит спецтокены как текст; BOS/шаблон сервер добавит сам при евале).
            using var content = JsonContent(new JsonObject { ["content"] = text, ["add_special"] = false });
            using var response = await _http.PostAsync(Url("tokenize"), content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
                {
                    return tokens.GetArrayLength();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw; // отмена не должна уходить во второй запрос и маскироваться под «сервер молчит»
        }
        catch
        {
        }

        try
        {
            using var content = JsonContent(new JsonObject { ["prompt"] = text, ["add_special"] = false });
            using var response = await _http.PostAsync(Url("api/extra/tokencount"), content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("value", out var value))
                {
                    return value.GetInt32();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// Парсинг нативного /completion (legacy): tokens_evaluated (промпт) / tokens_predicted
    /// (генерация) → TokenUsage. В стриминге каждое чанк несёт актуальные значения.
    /// </summary>
    private static TokenUsage? ParseNativeUsage(JsonElement root)
    {
        int? promptTokens = root.TryGetProperty("tokens_evaluated", out var prompt) && prompt.ValueKind == JsonValueKind.Number
            ? prompt.GetInt32() : null;
        int? completionTokens = root.TryGetProperty("tokens_predicted", out var completion) && completion.ValueKind == JsonValueKind.Number
            ? completion.GetInt32() : null;
        return promptTokens is null && completionTokens is null ? null : new TokenUsage(promptTokens, completionTokens);
    }

    private static StringContent JsonContent(JsonObject body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static StringContent BuildContent(string prompt, GenerationOptions options, bool stream, IReadOnlyList<string>? multimodalData = null)
    {
        var stop = new JsonArray();
        foreach (var token in options.Stop)
        {
            stop.Add(token);
        }

        // Нативный llama.cpp /completion (legacy): n_predict вместо max_tokens.
        // Мультимодальность: если есть multimodal_data — prompt объектный {prompt_string,
        // multimodal_data} (base64 1:1 с маркерами в prompt_string), иначе строка.
        // См. QwenMultimodalTest/findings.md.
        JsonNode promptNode;
        if (multimodalData is { Count: > 0 })
        {
            var mmArray = new JsonArray();
            foreach (var b64 in multimodalData)
            {
                mmArray.Add(b64);
            }
            promptNode = new JsonObject
            {
                ["prompt_string"] = prompt,
                ["multimodal_data"] = mmArray
            };
        }
        else
        {
            promptNode = prompt;
        }

        var body = new JsonObject
        {
            ["prompt"] = promptNode,
            ["n_predict"] = options.MaxTokens,
            ["temperature"] = options.Temperature,
            ["top_p"] = options.TopP,
            ["top_k"] = options.TopK,
            ["min_p"] = options.MinP,
            ["repeat_penalty"] = options.RepeatPenalty,
            ["stop"] = stop,
            ["stream"] = stream
        };
        if (options.Seed is { } seed)
        {
            body["seed"] = seed;
        }

        return JsonContent(body);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
