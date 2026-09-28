using System.Text;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Inference;

/// <summary>
/// Изолированные сервисные LLM-вызовы (суммаризация сессий, слои L1/L2/L3, извлечение фактов):
/// эндпоинт и семплер берутся из тех же настроек, что у основного хода (фабрики инжектируются
/// и вычисляются на каждый вызов — настройки живые), трафик пишется в traffic-журнал
/// (проверяемость постфактум), хук на чанк питает live-превью компакции.
///
/// Слот: вызов идёт на слоте СУММАРИЗУЕМОЙ сессии (параметр slotId вызывающего): контекст
/// сессии после сжатия меняется в любом случае, а отдельный сервисный слот раздувал бы
/// занятость единого KV-пула и замедлял промпт-обработку. slotId = null — сервер выбирает
/// сам (LRU): так ходит сессия, у которой слот не назначен (селектор «—»).
///
/// Результат структурных вызовов достаётся через submit_result: null означает «модель не
/// вызвала инструмент» — вызывающий решает, бросать ли (компакция) или считать «данных нет»
/// (извлечение памяти).
/// </summary>
public sealed class ServiceCompletionClient
{
    /// <summary>Потолок генерации сервисного вызова — ждём максимум: размышления о результате могут съесть десятки тысяч токенов.</summary>
    public const int MaxTokens = 60000;

    private readonly Func<string> _endpoint;
    private readonly Func<GenerationOptions> _optionsFactory;
    private readonly Func<string, ICompletionSource> _createSource;

    public ServiceCompletionClient(
        Func<string> endpoint,
        Func<GenerationOptions> optionsFactory,
        Func<string, ICompletionSource>? createSource = null)
    {
        _endpoint = endpoint;
        _optionsFactory = optionsFactory;
        _createSource = createSource ?? (endpoint => new LlmCompletionClient(endpoint));
    }

    /// <summary>
    /// Стрим сырого вывода: буфер + хук на чанк + запись в traffic-журнал.
    /// slotId — слот llama.cpp (см. класс): вызов на слоте сессии, которую обслуживаем.
    /// </summary>
    public async Task<string> StreamAsync(
        string prompt, Action<string>? onChunk = null, CancellationToken cancellationToken = default,
        int? slotId = null)
    {
        var raw = new StringBuilder();
        using (var client = _createSource(_endpoint()))
        {
            var options = _optionsFactory();
            options.IdSlot = slotId;
            await foreach (var chunk in client.StreamAsync(prompt, options, cancellationToken: cancellationToken))
            {
                raw.Append(chunk);
                onChunk?.Invoke(chunk);
            }
        }
        var output = raw.ToString();
        TrafficLog.Log(prompt, output);
        return output;
    }

    /// <summary>Структурный вызов: промпт рендерится через submit_result-обёртку, результат извлекается структурно.</summary>
    public async Task<string?> CompleteStructuredAsync(
        string userContent, string? system = null,
        Action<string>? onChunk = null, CancellationToken cancellationToken = default,
        int? slotId = null)
    {
        var prompt = StructuredCompletion.Render(userContent, system);
        var output = await StreamAsync(prompt, onChunk, cancellationToken, slotId);
        return StructuredCompletion.ExtractResult(output);
    }
}
