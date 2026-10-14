namespace QwenPlayground.Core.Inference;

/// <summary>
/// Сервер отверг запрос: промпт (+ max_tokens) не влезает в окно контекста (HTTP 400
/// с сигнатурой переполнения в теле). Отдельный тип — оркестратор хода (TurnPipeline)
/// распознаёт его и делает принудительную компакцию + 1 retry, вместо того чтобы
/// показывать сырую ошибку. Другие 400 (битый запрос, битая картинка) — обычные
/// HttpRequestException, сюда не попадают.
///
/// Почему это нужно: точный размер image-токенов сервер не экспозирует (флаги
/// --image-min-tokens/--image-max-tokens не видны через API), поэтому бюджет-гварда
/// считает картинки по оценке (ImageTokensMax). Если оценка занизена — сервер скажет
/// правду 400-м, и мы реагируем, а не ломаемся.
/// </summary>
public sealed class ContextOverflowException : Exception
{
    public ContextOverflowException(string serverMessage)
        : base($"Prompt exceeds the server's context window. {serverMessage}")
    {
    }

    /// <summary>
    /// Сигнатура переполнения в теле 400 от llama.cpp-сервера
    /// («prompt (N tokens) + max_tokens (M) exceeds the available context size (K tokens)»).
    /// Остальные 400 — не переполнение.
    /// </summary>
    public static bool IsContextOverflowBody(string? body)
    {
        return body is not null &&
               (body.Contains("exceeds the available context", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("context size", StringComparison.OrdinalIgnoreCase));
    }
}
