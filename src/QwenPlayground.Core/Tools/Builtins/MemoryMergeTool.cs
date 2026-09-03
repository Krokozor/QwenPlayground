using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Слияние двух фактов в один (дедупликация): оба удаляются, на их месте — объединённый
/// факт с содержимым обоих. Категория и вайб пересчитываются пробой компаньон-модели.
/// </summary>
[Tool("memory_merge", "Merge two memory items into one (deduplication): both are deleted, a combined fact replaces them. The merged fact is re-classified.")]
public sealed class MemoryMergeTool : AgentTool
{
    [ToolParameter("Id of the first memory item (from memory_list)", Required = true)]
    public string IdA { get; set; } = string.Empty;

    [ToolParameter("Id of the second memory item (from memory_list)", Required = true)]
    public string IdB { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (!AppSettings.Get().MemoryEnabled)
        {
            return MemoryToolGate.DisabledMessage;
        }
        var idA = IdA.Trim();
        var idB = IdB.Trim();
        if (idA.Length == 0 || idB.Length == 0 || idA == idB)
        {
            return "memory_merge: provide two different, non-empty ids.";
        }

        var store = new MemoryStore();
        var itemA = store.Get(idA);
        var itemB = store.Get(idB);
        if (itemA is null || itemB is null)
        {
            return $"memory_merge: one of the ids not found ({idA}={itemA is not null}, {idB}={itemB is not null}).";
        }

        var merged = store.Add(itemA.Content + "\n" + itemB.Content, source: "merge");
        store.Remove(idA);
        store.Remove(idB);
        await MemoryClassifier.EnrichAsync(
            merged, AppSettings.Get().CompanionEndpoint, cancellationToken: cancellationToken);
        if (merged.HasSemanticLayers)
        {
            store.Update(merged);
        }
        return $"Merged {idA} + {idB} → {merged.Id}. Filed as: " +
               $"{MemoryClassifier.TopName(merged.CategoryLayers)} {MemoryClassifier.TopEmojiOf(merged.EmojiLayers)}";
    }
}
