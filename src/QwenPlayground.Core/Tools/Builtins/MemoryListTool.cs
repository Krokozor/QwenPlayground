using System.Text;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>Список памяти: id, категория, эмодзи, заголовок — для просмотра и менеджмента.</summary>
[Tool("memory_list", "List all long-term memories (id, category, emoji, title). Use for memory management: spotting duplicates and deciding what to merge or delete.")]
public sealed class MemoryListTool : AgentTool
{
    // Кап на число фактов в выдаче: память растёт (nag'и подталкивают менеджмент),
    // полный список в один ответ может съесть заметный кусок контекста.
    private const int MaxItems = 200;

    private readonly string? _directory;

    public MemoryListTool()
    {
    }

    /// <summary>Инъекция каталога памяти — для тестов (боевой путь использует дефолт воркспейса).</summary>
    public MemoryListTool(string? directory) => _directory = directory;

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (!AppSettings.Get().MemoryEnabled)
        {
            return Task.FromResult(MemoryToolGate.DisabledMessage);
        }
        var items = new MemoryStore(_directory).List();
        if (items.Count == 0)
        {
            return Task.FromResult("Memory is empty.");
        }
        var builder = new StringBuilder();
        builder.AppendLine($"Total memories: {items.Count}. Format: [id] (filed as) — title");
        foreach (var item in items.Take(MaxItems))
        {
            var title = item.Content.Length <= 100 ? item.Content : item.Content[..100] + "…";
            var filed = item.HasSemanticLayers
                ? $"{MemoryClassifier.TopName(item.CategoryLayers)} {MemoryClassifier.TopEmojiOf(item.EmojiLayers)}"
                : "?";
            builder.AppendLine($"[{item.Id}] ({filed.Trim()}) — {title}");
        }
        if (items.Count > MaxItems)
        {
            builder.AppendLine($"... ({items.Count - MaxItems} more — do memory management: merge/delete to shrink)");
        }
        return Task.FromResult(builder.ToString());
    }
}
