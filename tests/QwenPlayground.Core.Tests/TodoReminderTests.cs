using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Напоминатель TODO в state-блоке: двухфазность (Peek чистый, OnRendered двигает счётчик),
/// детект изменения (агент/владелец), периодический интервал, изоляция пер-сессия.
/// Симуляция хода: Peek() → OnRendered() (реальный рендер); превью — только Peek().
/// </summary>
public sealed class TodoReminderTests : IDisposable
{
    private readonly string _dirA;
    private readonly string _dirB;
    private string _currentDir;
    private int _interval = 3;

    public TodoReminderTests()
    {
        _dirA = Path.Combine(Path.GetTempPath(), "qwen_todo_rem_a_" + Guid.NewGuid().ToString("N"));
        _dirB = Path.Combine(Path.GetTempPath(), "qwen_todo_rem_b_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dirA);
        Directory.CreateDirectory(_dirB);
        _currentDir = _dirA;
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _dirA, _dirB })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private TodoReminder Reminder() => new(() => _currentDir, () => _interval);

    private void SaveList(string dir, params (string text, bool done)[] items)
    {
        var list = new TodoList();
        foreach (var (text, done) in items)
        {
            list.Items.Add(new TodoItem { Text = text, Done = done });
        }
        new TodoStore(dir).Save(list, TodoStore.SourceAgent);
    }

    /// <summary>Один реальный шаг: peek (собрал бы блок) + onRendered (рендер в модель).</summary>
    private TodoSnapshot? Step(TodoReminder reminder)
    {
        var snapshot = reminder.Peek();
        reminder.OnRendered();
        return snapshot;
    }

    [Fact]
    public void NoFile_NoReminder()
    {
        var reminder = Reminder();
        Assert.Null(Step(reminder));
        Assert.Null(Step(reminder));
    }

    [Fact]
    public void FirstSight_Reminds_WithList()
    {
        SaveList(_dirA, ("goal one", false), ("goal two", true));
        var reminder = Reminder();

        var snapshot = Step(reminder);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Lines.Count);
        Assert.Equal("1. [ ] goal one", snapshot.Lines[0]);
        Assert.Equal("2. [x] goal two", snapshot.Lines[1]);
        // Первый раз — «изменилось», источник agent — подписывать не нужно.
        Assert.True(snapshot.Changed);
        Assert.Null(snapshot.Source);
    }

    [Fact]
    public void Unchanged_Silent_UntilInterval()
    {
        SaveList(_dirA, ("goal", false));
        var reminder = Reminder();
        _interval = 3;

        Step(reminder); // первый раз — напоминание
        Assert.Null(Step(reminder)); // шаг 2: без изменений, не время
        Assert.Null(Step(reminder)); // шаг 3: без изменений, не время
        var periodic = Step(reminder); // шаг 4: прошло 3 шага с последнего напоминания

        Assert.NotNull(periodic);
        Assert.False(periodic!.Changed);
        Assert.Equal("periodic reminder", periodic.Source);
    }

    [Fact]
    public void AgentChange_RemindsImmediately_NextStep_NoSource()
    {
        SaveList(_dirA, ("goal one", false));
        var reminder = Reminder();
        Step(reminder); // показан

        SaveList(_dirA, ("goal one", false), ("goal two", false)); // агент добавил
        var snapshot = Step(reminder);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Changed);
        Assert.Null(snapshot.Source); // агент — сам знает, что изменил
        Assert.Equal(2, snapshot.Lines.Count);
    }

    [Fact]
    public void UserChange_RemindsImmediately_WithUserSource()
    {
        SaveList(_dirA, ("goal one", false));
        var reminder = Reminder();
        Step(reminder);

        var list = new TodoStore(_dirA).Load()!;
        list.Items.Add(new TodoItem { Text = "user goal" });
        new TodoStore(_dirA).Save(list, TodoStore.SourceUser); // владелец из UI

        var snapshot = Step(reminder);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Changed);
        Assert.Equal("edited by user (not by the agent)", snapshot.Source);
    }

    [Fact]
    public void PeekIsPure_MultiplePeeksDoNotConsume()
    {
        SaveList(_dirA, ("goal", false));
        var reminder = Reminder();
        _interval = 5;

        Step(reminder); // показан, счётчик=1
        // Превью/подсчёт токенов: несколько Peek без OnRendered — счётчик не двигается.
        Assert.Null(reminder.Peek());
        Assert.Null(reminder.Peek());
        Assert.Null(reminder.Peek());

        // Реальные шаги 2 и 3 — всё ещё не время (интервал 5).
        Assert.Null(Step(reminder));
        Assert.Null(Step(reminder));
    }

    [Fact]
    public void IntervalZero_OnlyOnChange()
    {
        SaveList(_dirA, ("goal", false));
        var reminder = Reminder();
        _interval = 0;

        Step(reminder); // первый раз
        for (var i = 0; i < 50; i++)
        {
            Assert.Null(Step(reminder)); // без изменений и без периодики — тишина
        }

        SaveList(_dirA, ("goal", true)); // изменение (done) — сразу
        var snapshot = Step(reminder);
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Changed);
    }

    [Fact]
    public void SessionSwitch_StatesAreIsolated()
    {
        SaveList(_dirA, ("goal A", false));
        SaveList(_dirB, ("goal B", false));
        var reminder = Reminder();

        Step(reminder); // A показан
        _currentDir = _dirB;
        var bFirst = Step(reminder); // B — первый раз для B
        Assert.NotNull(bFirst);
        Assert.Contains("goal B", bFirst!.Lines[0]);

        _currentDir = _dirA;
        Assert.Null(Step(reminder)); // A: без изменений, счётчик A не смешался с B
    }

    [Fact]
    public void ListBecameEmpty_ResetsState()
    {
        SaveList(_dirA, ("goal", false));
        var reminder = Reminder();
        Step(reminder);

        new TodoStore(_dirA).Save(new TodoList(), TodoStore.SourceUser); // очистка
        Assert.Null(Step(reminder)); // нет списка — тишина

        SaveList(_dirA, ("new goal", false));
        var snapshot = Step(reminder);
        Assert.NotNull(snapshot); // новый список — снова «первый раз»
        Assert.True(snapshot!.Changed);
    }

    [Fact]
    public void AllDone_NagsToReportAndClear_OnEveryReminder_UntilCleared()
    {
        SaveList(_dirA, ("goal one", true), ("goal two", true));
        var reminder = Reminder();
        _interval = 3;

        var first = Step(reminder);
        Assert.NotNull(first);
        Assert.Equal(
            "ALL GOALS DONE — report completion to the owner, then clear the list (TODO_manage remove or the UI «очистить» button)",
            first!.Source);

        // Пока список все-выполненный — периодические напоминания тоже несут призыв
        // (nag до очистки): выполненные цели не должны молчаливо висеть.
        var periodic = Step(reminder);
        Assert.Null(periodic); // шаг 2: не время
        Assert.Null(Step(reminder)); // шаг 3: не время
        var nag = Step(reminder); // шаг 4: периодика
        Assert.NotNull(nag);
        Assert.StartsWith("ALL GOALS DONE", nag!.Source);

        // Очистили список — тишина.
        new TodoStore(_dirA).Save(new TodoList(), TodoStore.SourceAgent);
        Assert.Null(Step(reminder));
    }

    [Fact]
    public void AllDone_TakesPriorityOverUserSource()
    {
        var list = new TodoList { Items = { new TodoItem { Text = "goal", Done = true } } };
        new TodoStore(_dirA).Save(list, TodoStore.SourceUser); // владелец сам поставил последнюю галку
        var reminder = Reminder();

        var snapshot = Step(reminder);

        Assert.NotNull(snapshot);
        Assert.StartsWith("ALL GOALS DONE", snapshot!.Source);
    }

    [Fact]
    public void LongList_CappedAtFifteen_WithMoreMarker()
    {
        var items = new (string, bool)[20];
        for (var i = 0; i < 20; i++)
        {
            items[i] = ($"goal {i + 1}", false);
        }
        SaveList(_dirA, items);
        var reminder = Reminder();

        var snapshot = Step(reminder);

        Assert.NotNull(snapshot);
        Assert.Equal(16, snapshot!.Lines.Count); // 15 пунктов + «+5 more»
        Assert.Equal("+5 more", snapshot.Lines[^1]);
    }
}
