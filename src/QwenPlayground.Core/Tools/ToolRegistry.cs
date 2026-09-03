using System.Reflection;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;

namespace QwenPlayground.Core.Tools;

/// <summary>
/// Реестр инструментов агента: имя → <see cref="ToolEntry"/> (определение + исполнитель).
/// Источники — <see cref="IToolProvider"/>: встроенная рефлексия по [Tool]-классам
/// (<see cref="ReflectionToolProvider"/>), динамические инструменты MCP, плагины —
/// все регистрируются одинаково, реестр не знает, откуда пришёл инструмент.
///
/// Публичный контракт: <see cref="Definitions"/> для рекламы в промпте,
/// <see cref="ExecuteDetailedAsync"/> для исполнения (с опциональной финализацией —
/// см. <see cref="ToolEntry.Execute"/>). Дубликат имени — ошибка регистрации, не
/// молчаливый last-wins: спрятанный инструмент — потерянная способность агента.
/// </summary>
public sealed class ToolRegistry
{
    // Порядок определений стабилен (Ordinal) — промпт не «дышит» между ходами.
    private readonly Dictionary<string, ToolEntry> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ToolDefinition> _definitions = new();

    /// <summary>Классический сценарий: встроенные инструменты из сборок (пусто → Core).</summary>
    public ToolRegistry(params Assembly[] assemblies)
        : this((assemblies.Length > 0 ? assemblies : [typeof(AgentTool).Assembly])
               .Select(assembly => new ReflectionToolProvider(assembly)))
    {
    }

    public ToolRegistry(IEnumerable<IToolProvider> providers)
    {
        foreach (var provider in providers)
        {
            foreach (var entry in provider.Discover())
            {
                Register(entry);
            }
        }
    }

    /// <summary>
    /// Зарегистрировать/добавить инструмент (точка для MCP-клиента и плагинов).
    /// Дубликат имени — исключение: молчаливый last-wins спрятал бы способность.
    /// </summary>
    public void Register(ToolEntry entry)
    {
        if (!_tools.TryAdd(entry.Definition.Name, entry))
        {
            throw new InvalidOperationException(
                $"duplicate tool name '{entry.Definition.Name}': уже зарегистрирован");
        }
        _definitions.Add(entry.Definition);
        // Регистрации единичны (старт + подключение MCP) — пересорт дешёв, зато порядок
        // определений в промпте всегда стабилен и не «дышит» между ходами.
        _definitions.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
    }

    public IReadOnlyList<ToolDefinition> Definitions => _definitions;

    /// <summary>Определения одной группы (полки): Core — базовый набор, Browser/CSharp — активируемые.</summary>
    public IReadOnlyList<ToolDefinition> DefinitionsByGroup(ToolGroup group) =>
        _definitions.Where(d => d.Group == group).ToList();

    public async Task<string> ExecuteAsync(string name, JsonObject arguments, ToolContext context, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteDetailedAsync(name, arguments, context, cancellationToken);
        return result.Text;
    }

    /// <summary>
    /// Выполнение инструмента. ToolExecutionResult.Tool ненулевой, только если инструменту
    /// нужен этап финализации (AgentTool.FinalizeAsync — после добавления tool-сообщения
    /// в разговор с известным стабильным ID).
    /// </summary>
    public async Task<ToolExecutionResult> ExecuteDetailedAsync(string name, JsonObject arguments, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(name, out var entry))
        {
            return new ToolExecutionResult($"Error: unknown tool '{name}'", null);
        }
        try
        {
            return await entry.Execute(arguments, context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Отмена хода — не ошибка инструмента: иначе она превратится в «Error: ...»,
            // агентный цикл добавит это как ответ tool и продолжит работу с отменённым токеном.
            throw;
        }
        catch (Exception exception)
        {
            return new ToolExecutionResult($"Error: {exception.Message}", null);
        }
    }
}
