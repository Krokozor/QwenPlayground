using System.Text;
using QwenPlayground.Core.Memory;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>Читает полный факт по id — нужно для менеджмента памяти (дедуп, слияние).</summary>
[Tool("memory_get", "Read a full memory item by its id (from memory_list). Use to inspect a fact before merging or deleting it.")]
public sealed class MemoryGetTool : AgentTool
{
    [ToolParameter("Id of the memory item (from memory_list)", Required = true)]
    public string Id { get; set; } = string.Empty;

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var id = Id.Trim();
        if (id.Length == 0)
        {
            return Task.FromResult("memory_get: empty id. List ids with memory_list.");
        }
        var item = new MemoryStore().Get(id);
        if (item is null)
        {
            return Task.FromResult($"Memory {id} not found.");
        }
        var builder = new StringBuilder();
        builder.AppendLine($"Id: {item.Id}");
        builder.AppendLine($"Created: {item.CreatedAt:yyyy-MM-dd HH:mm}");
        builder.AppendLine($"Source: {item.Source}");
        if (item.HasSemanticLayers)
        {
            builder.AppendLine($"Filed as: {MemoryClassifier.TopName(item.CategoryLayers)} {MemoryClassifier.TopEmojiOf(item.EmojiLayers)}");
        }
        else
        {
            builder.AppendLine("Classification: pending (recall works by text until then)");
        }
        builder.AppendLine("Content:");
        builder.Append(item.Content);
        return Task.FromResult(builder.ToString());
    }
}
