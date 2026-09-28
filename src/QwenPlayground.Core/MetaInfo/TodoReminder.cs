using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.MetaInfo;

/// <summary>
/// Снапшот TODO-списка для state-блока: строки пунктов (формат «1. [ ] текст», те же
/// 1-based индексы, что у TODO_manage) + источник последнего изменения и тип напоминания.
/// </summary>
public sealed class TodoSnapshot
{
    /// <summary>Строки списка (одна на пункт, поле todo= повторяется в блоке).</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>
    /// Строка-источник (поле todo_src=): «edited by user», «periodic reminder» или null
    /// (изменил агент — сам знает). null — поле не рендерится.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>Список изменился со последнего напоминания (в отличие от периодического).</summary>
    public bool Changed { get; init; }
}

/// <summary>
/// Напоминатель TODO-списка в state-блоке: показывает список (a) сразу на шаге после
/// изменения (агент тулом или владелец из UI — источник подписывается) и (b) периодически,
/// каждые TodoReminderInterval шагов (настройка; 0 = только при изменении).
///
/// Двухфазный, по образцу MemorySurfacer: <see cref="Peek"/> — чистое «показывать ли в
/// следующем рендере» (вызывается из StateBlockBuilder.Build — в т.ч. превью/подсчёт
/// токенов, без побочных эффектов); <see cref="OnRendered"/> — вызывается владельцем ТОЛЬКО
/// после реального рендера в модель (TurnPipeline.StateProvider): сдвигает счётчик шагов
/// и помечает показанное. Превью не двигает счётчик — напоминание не «сгорает» зря.
///
/// Состояние пер-сессия (каталог): переключение сессий не смешивает счётчики.
/// </summary>
public sealed class TodoReminder
{
    /// <summary>Кап напоминания: пунктов в state-блоке (остальные — «+N more»).</summary>
    private const int MaxLines = 15;

    /// <summary>Кап длины одного пункта в напоминании.</summary>
    private const int MaxLineLength = 120;

    private sealed class RemState
    {
        public string? LastShownHash;
        public int StepsSinceReminder;
    }

    private readonly Func<string> _sessionDir;
    private readonly Func<int> _interval;
    private readonly Dictionary<string, RemState> _bySession = new(StringComparer.OrdinalIgnoreCase);

    public TodoReminder(Func<string> sessionDir, Func<int> interval)
    {
        _sessionDir = sessionDir;
        _interval = interval;
    }

    /// <summary>
    /// Чистый peek: нужно ли напоминание в следующем рендере (и какой). null — тишина.
    /// Не мутирует состояние: превью/подсчёт токенов могут звать сколько угодно раз.
    /// </summary>
    public TodoSnapshot? Peek()
    {
        var (list, state) = Current();
        if (list is null || list.Items.Count == 0)
        {
            return null;
        }
        var hash = TodoStore.ContentHash(list);
        var changed = state.LastShownHash != hash; // первый раз (null) — тоже «изменилось»
        var periodic = _interval() > 0 && state.StepsSinceReminder >= _interval();
        if (!changed && !periodic)
        {
            return null;
        }
        return BuildSnapshot(list, changed);
    }

    /// <summary>
    /// Реальный рендер в модель произошёл: сдвинуть счётчик шагов и (если в этом рендере
    /// напоминание было) пометить текущий отпечаток показанным. Вызывать ТОЛЬКО после
    /// реального рендера (TurnPipeline), не из превью.
    /// </summary>
    public void OnRendered()
    {
        var (list, state) = Current();
        if (list is null || list.Items.Count == 0)
        {
            state.LastShownHash = null;
            state.StepsSinceReminder = 0;
            return;
        }
        var hash = TodoStore.ContentHash(list);
        var changed = state.LastShownHash != hash;
        var periodic = _interval() > 0 && state.StepsSinceReminder >= _interval();
        state.StepsSinceReminder++;
        if (changed || periodic)
        {
            state.LastShownHash = hash;
        }
    }

    private (TodoList? list, RemState state) Current()
    {
        var dir = _sessionDir();
        var list = new TodoStore(dir).Load();
        if (!_bySession.TryGetValue(dir, out var state))
        {
            state = new RemState();
            _bySession[dir] = state;
        }
        return (list, state);
    }

    private static TodoSnapshot BuildSnapshot(TodoList list, bool changed)
    {
        var lines = new List<string>(list.Items.Count);
        for (var i = 0; i < list.Items.Count && i < MaxLines; i++)
        {
            var text = list.Items[i].Text;
            if (text.Length > MaxLineLength)
            {
                text = text[..MaxLineLength] + "…";
            }
            lines.Add($"{i + 1}. [{(list.Items[i].Done ? "x" : " ")}] {text}");
        }
        if (list.Items.Count > MaxLines)
        {
            lines.Add($"+{list.Items.Count - MaxLines} more");
        }
        // «Все цели выполнены» — призыв к действию: отрапортовать владельцу и почистить
        // список, чтобы выполненные цели не висели в напоминаниях. Показываем при ЛЮБОМ
        // напоминании, пока список все-выполненный (в т.ч. периодическом) — nag до очистки.
        var allDone = list.Items.All(i => i.Done);
        var source = allDone
            ? "ALL GOALS DONE — report completion to the owner, then clear the list (TODO_manage remove or the UI «очистить» button)"
            : changed
                // Источник подписываем только когда это владелец: агент, изменивший список
                // сам, и так знает, что сделал (иначе «а хули список поменялся»).
                ? (list.LastModifiedBy == TodoStore.SourceUser ? "edited by user (not by the agent)" : null)
                // Периодическое (без изменения) помечаем, чтобы не приняли за изменение.
                : "periodic reminder";
        return new TodoSnapshot { Lines = lines, Source = source, Changed = changed };
    }
}
