using System.Text;

namespace QwenPlayground.Core.Memory;

/// <summary>
/// Три слоя долгосрочной памяти main-агента (по глубине): L3 — самые свежие события,
/// L2 — средний слой, L1 — самый старый дистиллят. Живут в sessions/main/layers.json
/// и инжектятся в системный промпт при каждой сборке — это часть идентичности модели,
/// поэтому переживает рестарты и rebuild_self. Пустые слои не рендерятся.
/// </summary>
public sealed class LayerMemory
{
    public string L1 { get; set; } = string.Empty;
    public string L2 { get; set; } = string.Empty;
    public string L3 { get; set; } = string.Empty;

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(L1) && string.IsNullOrWhiteSpace(L2) && string.IsNullOrWhiteSpace(L3);

    /// <summary>
    /// Блок памяти для системного промпта: объяснение слоёв + непустые слои.
    /// Без служебных токенов — обычный текст, безопасный для саморедакции.
    /// </summary>
    public string ToPromptBlock()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("— Долгосрочная память (слои L1–L3) —");
        builder.AppendLine(
            "Это твоя слоистая память по глубине: L3 — самые свежие события, L2 — средний слой, " +
            "L1 — самый старый дистиллят. Слои — часть твоей идентичности и преемственности: " +
            "опирайся на них при работе, не выбрасывай важное и не повторяй уже решённое.");
        AppendLayer(builder, "Слой L1", L1);
        AppendLayer(builder, "Слой L2", L2);
        AppendLayer(builder, "Слой L3", L3);
        return builder.ToString().TrimEnd();
    }

    private static void AppendLayer(StringBuilder builder, string title, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }
        builder.AppendLine().Append('[').Append(title).AppendLine("]").AppendLine(content.Trim());
    }
}