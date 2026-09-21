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
///
/// Потоки: мутации (MCP-регистрация/отключение) идут с фоновых потоков, а
/// <see cref="Definitions"/> читает сборка промпта и UI — с других. Поэтому
/// <c>_tools</c> — ConcurrentDictionary, а <c>_definitions</c> — неизменяемый
/// снапшот (массив), атомарно заменяемый при мутации: читатель никогда не видит
/// полуобновлённый список и не берёт лок.
/// </summary>
public sealed class ToolRegistry
{
    // Порядок определений стабилен (Ordinal) — промпт не «дышит» между ходами.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ToolEntry> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _mutationLock = new();
    private volatile ToolDefinition[] _definitions = [];

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
        lock (_mutationLock)
        {
            if (!_tools.TryAdd(entry.Definition.Name, entry))
            {
                throw new InvalidOperationException(
                    $"duplicate tool name '{entry.Definition.Name}': уже зарегистрирован");
            }
            // Регистрации единичны (старт + подключение MCP) — пересорт снапшота дешёв,
            // зато порядок определений в промпте всегда стабилен и не «дышит» между ходами.
            RepublishDefinitions();
        }
    }

    /// <summary>
    /// Пытается зарегистрировать инструмент. Возвращает false при коллизии имени
    /// (без исключения) — для MCP-тулов, где коллизия с built-in допустима (warn + skip).
    /// </summary>
    public bool TryRegister(ToolEntry entry)
    {
        lock (_mutationLock)
        {
            if (!_tools.TryAdd(entry.Definition.Name, entry))
            {
                return false;
            }
            RepublishDefinitions();
            return true;
        }
    }

    /// <summary>Удалить все инструменты с указанным префиксом (например, "blender_").</summary>
    public int UnregisterByPrefix(string prefix)
    {
        lock (_mutationLock)
        {
            var removed = 0;
            foreach (var name in _tools.Keys
                         .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                if (_tools.TryRemove(name, out _))
                {
                    removed++;
                }
            }
            if (removed > 0)
            {
                RepublishDefinitions();
            }
            return removed;
        }
    }

    /// <summary>
    /// Новый отсортированный снапшот определений из текущего состояния <c>_tools</c>.
    /// Вызывается только под <c>_mutationLock</c>; замена volatile-поля атомарна,
    /// поэтому читатели (Definitions/DefinitionsByGroup) работают без лока.
    /// </summary>
    private void RepublishDefinitions()
    {
        _definitions = _tools.Values
            .Select(t => t.Definition)
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
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
