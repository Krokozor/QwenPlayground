using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Сохраняет долговременный факт в память агента (memories/ + index.md).
///
/// Принцип системы: модель-автор пишет ТОЛЬКО текст — ноль таксономической нагрузки.
/// Категорию/вайб назначает компаньон-модель пробами (см. EnrichAsync): лучший случай —
/// факт сразу со слоями; не успела (короткий таймаут) или недоступна — Flush на heartbeat
/// догонит, до тех пор реколл работает текстовым overlap'ом.
/// </summary>
[Tool("memory_add", "Save a long-term fact to agent memory: decisions, error fixes, preferences, important identifiers. Write only the fact itself, self-contained — classification is done by the system.")]
public sealed class MemoryAddTool : AgentTool
{
    [ToolParameter("The fact to remember. Self-contained: it must make sense without the conversation context.", Required = true)]
    public string Content { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (!AppSettings.Get().MemoryEnabled)
        {
            return MemoryToolGate.DisabledMessage;
        }
        var content = Content.Trim();
        if (content.Length == 0)
        {
            return "memory_add: the fact is empty — describe what to remember.";
        }
        var store = new MemoryStore();
        var item = store.Add(content, source: "agent");
        context.OnFactSaved?.Invoke(item);

        // Классификация другой моделью. Короткий таймаут: латентность пробы не должна
        // платиться ходом агента; сбой — тоже не ошибка сохранения (Flush догонит).
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await MemoryClassifier.EnrichAsync(
                item, AppSettings.Get().CompanionEndpoint, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // классификация недоступна — факт остаётся без слоёв, это штатно
        }

        if (item.HasSemanticLayers)
        {
            store.Update(item);
        }
        return $"Memory saved: {item.Id} (memories/{item.Id}.json). Index updated." +
               (item.HasSemanticLayers
                   ? $" Filed as: {MemoryClassifier.TopName(item.CategoryLayers)} {MemoryClassifier.TopEmojiOf(item.EmojiLayers)}."
                   : "");
    }
}
