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

    private readonly string? _directory;

    public MemoryAddTool()
    {
    }

    /// <summary>Инъекция каталога памяти — для тестов (боевой путь использует дефолт воркспейса).</summary>
    public MemoryAddTool(string? directory) => _directory = directory;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        // memory_add — базовый инструмент-«записка»: работает ВСЕГДА, независимо от
        // мастер-переключателя памяти. Выкл памяти гасит «умную» часть (реколл/слои/дедуп),
        // но не саму запись факта.
        var content = Content.Trim();
        if (content.Length == 0)
        {
            return "memory_add: the fact is empty — describe what to remember.";
        }
        var store = new MemoryStore(_directory);
        var item = store.Add(content, source: "agent");
        context.OnFactSaved?.Invoke(item);

        // Классификация другой моделью — только когда память включена. Выкл — факт
        // сохраняется без слоёв БЕЗ ожидания 8-секундного таймаута (компаньон, скорее
        // всего, недоступен — ровно поэтому память и выключена). Короткий таймаут:
        // латентность пробы не должна платиться ходом агента; сбой — не ошибка сохранения.
        if (AppSettings.Get().MemoryEnabled)
        {
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
        }

        return $"Memory saved: {item.Id} (memories/{item.Id}.json). Index updated." +
               (item.HasSemanticLayers
                   ? $" Filed as: {MemoryClassifier.TopName(item.CategoryLayers)} {MemoryClassifier.TopEmojiOf(item.EmojiLayers)}."
                   : "");
    }
}
