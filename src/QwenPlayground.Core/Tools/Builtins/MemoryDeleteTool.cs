using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>Удаляет элемент памяти по id (после дедупликации или исправления).</summary>
[Tool("memory_delete", "Delete a memory item by its id (deduplication or correction). Ids are listed in memories/index.md.")]
public sealed class MemoryDeleteTool : AgentTool
{
    [ToolParameter("Id of the memory item (from memories/index.md)", Required = true)]
    public string Id { get; set; } = string.Empty;

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (!AppSettings.Get().MemoryEnabled)
        {
            return Task.FromResult(MemoryToolGate.DisabledMessage);
        }
        var id = Id.Trim();
        if (id.Length == 0)
        {
            return Task.FromResult("memory_delete: поле пусто — укажи id из memories/index.md.");
        }
        var removed = new MemoryStore().Remove(id);
        return Task.FromResult(removed
            ? $"Память {id} удалена."
            : $"Память {id} не найдена.");
    }
}
