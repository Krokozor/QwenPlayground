using System.Text.Json.Nodes;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Тулы TODO_add/TODO_manage: работа с TODO.json сессии (ToolContext.SessionDir),
/// 1-based индексы, операции done/undone/remove, валидация, вывод нумерованного списка.
/// </summary>
public sealed class TodoToolsTests : IDisposable
{
    private readonly string _root;
    private readonly string _sessionDir;
    private readonly ToolRegistry _registry = new();
    private readonly ToolContext _context;

    public TodoToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qwen_todo_tools_" + Guid.NewGuid().ToString("N"));
        _sessionDir = Path.Combine(_root, "sessions", "test");
        Directory.CreateDirectory(_sessionDir);
        _context = new ToolContext(_root, sessionDir: _sessionDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private static JsonObject Arr(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }
        return new JsonObject { ["items"] = array };
    }

    private static JsonObject IntArr(params int[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }
        return new JsonObject { ["indices"] = array };
    }

    [Fact]
    public async Task Definitions_Registered()
    {
        var names = _registry.Definitions.Select(d => d.Name).ToList();
        Assert.Contains("TODO_add", names);
        Assert.Contains("TODO_manage", names);
    }

    [Fact]
    public async Task Add_CreatesFile_AndRendersNumberedList()
    {
        var result = await _registry.ExecuteAsync("TODO_add", Arr("fix the bug", "add tests"), _context);

        Assert.Contains("TODO (0/2 done):", result);
        Assert.Contains("1. [ ] fix the bug", result);
        Assert.Contains("2. [ ] add tests", result);

        var list = new TodoStore(_sessionDir).Load();
        Assert.NotNull(list);
        Assert.Equal(2, list!.Items.Count);
        Assert.Equal(TodoStore.SourceAgent, list.LastModifiedBy);
    }

    [Fact]
    public async Task Add_EmptyList_NoFileCreated()
    {
        var result = await _registry.ExecuteAsync("TODO_add", Arr("   ", ""), _context);

        Assert.Contains("пустой список", result);
        Assert.Null(new TodoStore(_sessionDir).Load());
    }

    [Fact]
    public async Task Manage_DoneUndoneRemove_WorkByOneBasedIndex()
    {
        await _registry.ExecuteAsync("TODO_add", Arr("a", "b", "c"), _context);

        var done = await _registry.ExecuteAsync("TODO_manage",
            new JsonObject { ["indices"] = new JsonArray(1, 3), ["operation"] = "done" }, _context);
        Assert.Contains("TODO (2/3 done):", done);
        Assert.Contains("1. [x] a", done);
        Assert.Contains("2. [ ] b", done);
        Assert.Contains("3. [x] c", done);

        var undone = await _registry.ExecuteAsync("TODO_manage",
            new JsonObject { ["indices"] = new JsonArray(1), ["operation"] = "undone" }, _context);
        Assert.Contains("1. [ ] a", undone);
        Assert.Contains("TODO (1/3 done):", undone);

        var removed = await _registry.ExecuteAsync("TODO_manage",
            new JsonObject { ["indices"] = new JsonArray(2), ["operation"] = "remove" }, _context);
        Assert.Contains("TODO (1/2 done):", removed);
        Assert.Contains("1. [ ] a", removed);
        Assert.Contains("2. [x] c", removed);
        Assert.DoesNotContain("[ ] b", removed); // «b» удалён, не просто сменил номер
    }

    [Fact]
    public async Task Manage_RemoveMultipleIndices_NoShiftBug()
    {
        // Регрессия: мутация по индексам в цикле сдвигала список — remove [1,2,3] из трёх
        // пунктов удалял только два. Реконструкция с фильтром не зависит от порядка,
        // дубликатов и сдвигов.
        await _registry.ExecuteAsync("TODO_add", Arr("a", "b", "c"), _context);

        var result = await _registry.ExecuteAsync("TODO_manage",
            new JsonObject { ["indices"] = new JsonArray(3, 1, 2), ["operation"] = "remove" }, _context);

        Assert.Contains("TODO (0/0 done):", result); // все три удалены (а не два, как при мутации)
        Assert.Null(new TodoStore(_sessionDir).Load()); // пустой список — файл удалён
    }

    [Fact]
    public async Task Manage_RemoveMultiple_KeepsUnlisted_WithDuplicatesAndGaps()
    {
        await _registry.ExecuteAsync("TODO_add", Arr("a", "b", "c", "d"), _context);

        var result = await _registry.ExecuteAsync("TODO_manage",
            new JsonObject { ["indices"] = new JsonArray(2, 2, 4), ["operation"] = "remove" }, _context);

        Assert.Contains("TODO (0/2 done):", result);
        Assert.Contains("1. [ ] a", result);
        Assert.Contains("2. [ ] c", result);
        Assert.DoesNotContain("[ ] b", result);
        Assert.DoesNotContain("[ ] d", result);
    }

    [Fact]
    public async Task Manage_DoneMultipleIndices_AllApplied()
    {
        await _registry.ExecuteAsync("TODO_add", Arr("a", "b", "c"), _context);

        var result = await _registry.ExecuteAsync("TODO_manage",
            new JsonObject { ["indices"] = new JsonArray(1, 1, 3), ["operation"] = "done" }, _context);

        Assert.Contains("TODO (2/3 done):", result);
        Assert.Contains("1. [x] a", result);
        Assert.Contains("2. [ ] b", result);
        Assert.Contains("3. [x] c", result);
    }

    [Fact]
    public async Task Manage_OutOfRangeIndex_SkippedAndReported()
    {
        await _registry.ExecuteAsync("TODO_add", Arr("a"), _context);

        var result = await _registry.ExecuteAsync("TODO_manage",
            new JsonObject { ["indices"] = new JsonArray(1, 5), ["operation"] = "done" }, _context);

        Assert.Contains("1. [x] a", result);
        Assert.Contains("5", result); // пропущенный индекс сообщён
    }

    [Fact]
    public async Task Manage_BadOperation_Rejected()
    {
        await _registry.ExecuteAsync("TODO_add", Arr("a"), _context);

        var result = await _registry.ExecuteAsync("TODO_manage",
            new JsonObject { ["indices"] = new JsonArray(1), ["operation"] = "explode" }, _context);

        Assert.Contains("неизвестная операция", result);
    }

    [Fact]
    public async Task Manage_EmptyList_FriendlyMessage()
    {
        var result = await _registry.ExecuteAsync("TODO_manage",
            new JsonObject { ["indices"] = new JsonArray(1), ["operation"] = "done" }, _context);

        Assert.Contains("пуст или отсутствует", result);
    }

    [Fact]
    public async Task NoSessionDir_GracefulError()
    {
        var context = new ToolContext(_root); // без sessionDir
        var result = await _registry.ExecuteAsync("TODO_add", Arr("a"), context);
        Assert.Contains("каталог сессии недоступен", result);
    }
}
