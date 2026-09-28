using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Общий вывод TODO-тулов: нумерованный список (1-based индексы — те, что принимает
/// TODO_manage) + счётчик выполненных. Формат совпадает со строками напоминания
/// в state-блоке (todo=), чтобы модель видела один и тот же список.
/// </summary>
internal static class TodoFormat
{
    public static string RenderList(TodoList list)
    {
        var done = list.Items.Count(i => i.Done);
        var lines = new List<string> { $"TODO ({done}/{list.Items.Count} done):" };
        for (var i = 0; i < list.Items.Count; i++)
        {
            lines.Add($"{i + 1}. [{(list.Items[i].Done ? "x" : " ")}] {list.Items[i].Text}");
        }
        return string.Join("\n", lines);
    }
}

/// <summary>
/// Добавить цели в TODO-список сессии (TODO.json в папке сессии; если файла нет —
/// создаётся). Список периодически напоминает себе в state-блоке; владелец может
/// править его вручную в панели TODO над чатом.
/// </summary>
[Tool("TODO_add", "Add goals to this session's TODO list (TODO.json in the session folder; created if absent). The list is reminded in the <state> block periodically and right after changes; the owner can edit it in the TODO panel above the chat. Returns the updated numbered list.")]
public sealed class TodoAddTool : AgentTool
{
    [ToolParameter("Goals to add (array of strings; empty/whitespace entries are skipped)", Required = true)]
    public List<string> Items { get; set; } = new();

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.SessionDir is null)
        {
            return Task.FromResult("TODO_add: каталог сессии недоступен — TODO-список не поддерживается в этом контексте.");
        }
        var store = new TodoStore(context.SessionDir);
        var list = store.LoadOrCreate();
        var added = 0;
        foreach (var item in Items ?? new List<string>())
        {
            var text = item?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                continue;
            }
            list.Items.Add(new TodoItem { Text = text });
            added++;
        }
        if (added == 0)
        {
            return Task.FromResult("TODO_add: пустой список целей — ничего не добавлено.");
        }
        store.Save(list, TodoStore.SourceAgent);
        return Task.FromResult(TodoFormat.RenderList(list));
    }
}

/// <summary>
/// Операция над пунктами TODO по 1-based индексам (нумерация — как в напоминании
/// state-блока и в выводе TODO_add): done — отметить выполненным, undone — снять
/// отметку, remove — удалить из списка. Индексы за пределами списка пропускаются.
/// </summary>
[Tool("TODO_manage", "Operate on TODO items by 1-based index (numbering as shown in the <state> reminder / TODO_add output). Operation: 'done' (mark completed), 'undone' (unmark), 'remove' (delete from list). Out-of-range indices are skipped. Returns the updated numbered list.")]
public sealed class TodoManageTool : AgentTool
{
    [ToolParameter("1-based indices of the items to operate on (array of integers)", Required = true)]
    public List<int> Indices { get; set; } = new();

    [ToolParameter("Operation to apply: 'done' | 'undone' | 'remove'", Required = true)]
    public string Operation { get; set; } = string.Empty;

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.SessionDir is null)
        {
            return Task.FromResult("TODO_manage: каталог сессии недоступен — TODO-список не поддерживается в этом контексте.");
        }
        var op = Operation?.Trim().ToLowerInvariant() ?? string.Empty;
        if (op is not ("done" or "undone" or "remove"))
        {
            return Task.FromResult("TODO_manage: неизвестная операция «" + Operation + "». Используй 'done', 'undone' или 'remove'.");
        }
        var store = new TodoStore(context.SessionDir);
        var list = store.Load();
        if (list is null || list.Items.Count == 0)
        {
            return Task.FromResult("TODO_manage: TODO-список пуст или отсутствует — нечего изменять (добавь цели через TODO_add).");
        }
        // Реконструкция с фильтром (а не мутация по индексам): не зависит от порядка
        // объявления индексов, дубликатов и сдвигов при удалении. Инпут сначала
        // приводим в порядок: дедуп + отброс неверных чисел (вне 1..Count).
        var valid = (Indices ?? new List<int>())
            .Where(i => i >= 1 && i <= list.Items.Count)
            .ToHashSet();
        var skipped = (Indices ?? new List<int>())
            .Distinct()
            .Where(i => i < 1 || i > list.Items.Count)
            .OrderBy(i => i)
            .ToList();
        var filtered = new List<TodoItem>(list.Items.Count);
        for (var i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            if (op == "remove")
            {
                if (!valid.Contains(i + 1))
                {
                    filtered.Add(item); // исключаем только указанные индексы
                }
            }
            else
            {
                if (valid.Contains(i + 1))
                {
                    item.Done = op == "done";
                }
                filtered.Add(item);
            }
        }
        list.Items = filtered;
        if (valid.Count == 0)
        {
            return Task.FromResult("TODO_manage: ни один индекс не применим (список из " + list.Items.Count +
                                    " пунктов, индексы 1.." + list.Items.Count + ").");
        }
        store.Save(list, TodoStore.SourceAgent);
        var result = TodoFormat.RenderList(list);
        if (skipped.Count > 0)
        {
            result += "\nПропущены индексы (вне списка): " + string.Join(", ", skipped);
        }
        return Task.FromResult(result);
    }
}
