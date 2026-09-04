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

        // Заголовки — в стиле «# Tools» эталонного шаблона: H1 на секцию, H2 на слой,
        // пустая строка между заголовком и содержимым. Порядок слоёв L1→L2→L3: промпт
        // течёт к чату «старая история → чуть новейшая → последняя → чат».
        var builder = new StringBuilder();
        builder.AppendLine("# Long-term memory (layers L1–L3)");
        builder.AppendLine();
        builder.AppendLine(
            "This is your layered memory by depth: L3 — the freshest events, L2 — the middle layer, " +
            "L1 — the oldest distillate. The layers are part of your identity and continuity: " +
            "rely on them while working, do not discard what matters and do not re-solve what is already solved.");
        AppendLayer(builder, "Layer L1", L1);
        AppendLayer(builder, "Layer L2", L2);
        AppendLayer(builder, "Layer L3", L3);
        return builder.ToString().TrimEnd();
    }

    private static void AppendLayer(StringBuilder builder, string title, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }
        builder.AppendLine().Append("## ").Append(title).AppendLine().AppendLine().AppendLine(content.Trim());
    }
}