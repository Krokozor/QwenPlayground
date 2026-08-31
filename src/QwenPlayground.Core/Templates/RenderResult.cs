namespace QwenPlayground.Core.Templates;

/// <summary>
/// Результат рендера chat-шаблона: строка промпта + мультимодальные данные (base64-картинки,
/// порядок = порядок маркеров в промпте). При отсутствии вложений MultimodalData пуст —
/// клиент шлёт обычный строковый prompt, не объектный.
/// </summary>
public sealed record RenderResult(string Prompt, IReadOnlyList<string> MultimodalData)
{
    public static RenderResult TextOnly(string prompt) => new(prompt, []);
}
