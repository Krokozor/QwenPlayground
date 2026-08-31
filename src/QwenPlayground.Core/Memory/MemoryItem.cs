namespace QwenPlayground.Core.Memory;

/// <summary>
/// Долговременный факт в памяти агента. Один файл на факт: memories/&lt;id&gt;.json.
/// Модель-автор пишет ТОЛЬКО текст: категоризация — целиком работа компаньон-модели
/// через семантические слои (CategoryLayers/EmojiLayers распределения, заполняются
/// логит-пробами — см. MemoryClassifier) и используются реколлом для overlap-скоринга.
/// Отдельных строковых Category/Emoji нет: выводимые имена считаются из распределений
/// (MemoryClassifier.TopName/TopEmojiOf), чтобы не плодить второй источник правды.
/// </summary>
public sealed class MemoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Сам факт — самодостаточный, понятен без контекста диалога.</summary>
    public string Content { get; set; } = string.Empty;
    /// <summary>Откуда: agent (memory_add), compaction (автоизвлечение) или codebase.</summary>
    public string Source { get; set; } = "agent";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Версия словаря/классификатора, которой посчитаны слои. Старая версия (или 0 — слоёв нет)
    /// означает, что слои устарели: flush-воркер переклассифицирует такой факт. Это «flush-механизм»
    /// NekoBot: словарь или модель-классификатор могут меняться, память само-залечивается на фоне.
    /// </summary>
    public int LayersVersion { get; set; }

    /// <summary>Распределение по категориям (буква A-Z → вероятность), заполняется пробой.</summary>
    public Dictionary<string, double> CategoryLayers { get; set; } = new();
    /// <summary>Распределение по эмодзи (символ → вероятность), заполняется пробой.</summary>
    public Dictionary<string, double> EmojiLayers { get; set; } = new();

    /// <summary>Есть ли семантические слои (можно ли скорить overlap'ом против запроса).</summary>
    public bool HasSemanticLayers => CategoryLayers.Count > 0 || EmojiLayers.Count > 0;
}
