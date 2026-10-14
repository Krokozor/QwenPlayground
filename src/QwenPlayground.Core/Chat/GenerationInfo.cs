namespace QwenPlayground.Core.Chat;

public sealed class GenerationInfo
{
    public required string Prompt { get; init; }
    public string RawOutput { get; set; } = string.Empty;
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }

    /// <summary>
    /// true — стрим этого хода закончился без stop-маркера (сервер закрыл поток досрочно):
    /// RawOutput может быть неполным (вплоть до незакрытого tool-call). Диагностика:
    /// крах/таймаут/перезапуск llama.cpp, а не «модель так ответила».
    /// </summary>
    public bool StreamAborted { get; set; }
}
