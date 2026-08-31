using System.Text.Json.Nodes;

namespace QwenPlayground.Core.Inference;

/// <summary>
/// Источник завершений, привязанный к одному эндпоинту: полный ответ, стрим,
/// точный подсчёт токенов. Абстракция над llama.cpp-клиентом — точка роста для
/// альтернативных бэкендов и тестовых заглушек.
///
/// Потребители не держат экземпляр, а принимают фабрику Func&lt;string, ICompletionSource&gt;
/// и создают источник под эндпоинт на вызов (эндпоинт — живая настройка; экземпляр
/// не потокобезопасен: LastUsage — состояние последнего ответа).
/// </summary>
public interface ICompletionSource : IDisposable
{
    /// <summary>Usage последнего ответа; null — сервер не отдал.</summary>
    TokenUsage? LastUsage { get; }

    /// <summary>Полный ответ (/completion legacy-формат): текст + usage.</summary>
    Task<CompletionResult> CompleteAsync(string prompt, GenerationOptions options, CancellationToken cancellationToken = default);

    /// <summary>Стрим чанков контента (SSE /completion); последний чанк с stop:true завершает перечисление.</summary>
    IAsyncEnumerable<string> StreamAsync(
        string prompt,
        GenerationOptions options,
        IReadOnlyList<string>? multimodalData = null,
        CancellationToken cancellationToken = default);

    /// <summary>Точное количество токенов текста; null — сервер не дал число.</summary>
    Task<int?> CountTokensAsync(string text, CancellationToken cancellationToken = default);
}
