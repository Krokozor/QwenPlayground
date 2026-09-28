using System.IO;
using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Sessions;

namespace QwenPlayground.App.Tests;

/// <summary>
/// TODO-панель: Reload не должен трогать файл (регрессия: сеттеры Text/IsDone при
/// перестроении коллекции вызывали SaveFromUi на частичной коллекции — файл затёрся
/// неполным списком с источником «user»). Пользовательские мутации (чекбокс, добавление,
/// удаление) пишут с источником «user».
/// </summary>
public sealed class TodoViewModelTests : IDisposable
{
    private readonly string _dir;

    public TodoViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qwen_todo_vm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    private static TodoList ThreeItems() => new()
    {
        Items =
        {
            new TodoItem { Text = "goal one", Done = false },
            new TodoItem { Text = "goal two", Done = true },
            new TodoItem { Text = "goal three", Done = false }
        }
    };

    [Fact]
    public void CtorReload_DoesNotClobberFile()
    {
        new TodoStore(_dir).Save(ThreeItems(), TodoStore.SourceAgent);

        var vm = new TodoViewModel(() => _dir); // конструктор → Reload

        Assert.Equal(3, vm.Items.Count);
        var onDisk = new TodoStore(_dir).Load();
        Assert.NotNull(onDisk);
        Assert.Equal(3, onDisk!.Items.Count); // не затёрт частичным списком
        Assert.Equal(TodoStore.SourceAgent, onDisk.LastModifiedBy); // источник не сменился на «user»
        Assert.Equal("goal two", onDisk.Items[1].Text);
        Assert.True(onDisk.Items[1].Done);
    }

    [Fact]
    public void ExplicitReload_AfterExternalChange_SyncsAndPreservesSource()
    {
        new TodoStore(_dir).Save(ThreeItems(), TodoStore.SourceAgent);
        var vm = new TodoViewModel(() => _dir);

        // «Агент» изменил список (внешний Save) — панель должна подхватить.
        var list = new TodoStore(_dir).Load()!;
        list.Items.Add(new TodoItem { Text = "goal four" });
        new TodoStore(_dir).Save(list, TodoStore.SourceAgent);

        vm.Reload();

        Assert.Equal(4, vm.Items.Count);
        var onDisk = new TodoStore(_dir).Load();
        Assert.Equal(4, onDisk!.Items.Count);
        Assert.Equal(TodoStore.SourceAgent, onDisk.LastModifiedBy);
    }

    [Fact]
    public void ToggleDone_SavesWithUserSource()
    {
        new TodoStore(_dir).Save(ThreeItems(), TodoStore.SourceAgent);
        var vm = new TodoViewModel(() => _dir);

        vm.Items[0].IsDone = true;

        var onDisk = new TodoStore(_dir).Load();
        Assert.Equal(3, onDisk!.Items.Count);
        Assert.True(onDisk.Items[0].Done);
        Assert.Equal(TodoStore.SourceUser, onDisk.LastModifiedBy);
    }

    [Fact]
    public void EditText_SavesWithUserSource()
    {
        new TodoStore(_dir).Save(ThreeItems(), TodoStore.SourceAgent);
        var vm = new TodoViewModel(() => _dir);

        vm.Items[2].Text = "renamed goal";

        var onDisk = new TodoStore(_dir).Load();
        Assert.Equal("renamed goal", onDisk!.Items[2].Text);
        Assert.Equal(TodoStore.SourceUser, onDisk.LastModifiedBy);
    }

    [Fact]
    public void AddItem_SavesWithUserSource()
    {
        new TodoStore(_dir).Save(ThreeItems(), TodoStore.SourceAgent);
        var vm = new TodoViewModel(() => _dir);
        vm.NewItemText = "new goal";

        vm.AddItemCommand.Execute(null);

        Assert.Equal(string.Empty, vm.NewItemText);
        var onDisk = new TodoStore(_dir).Load();
        Assert.Equal(4, onDisk!.Items.Count);
        Assert.Equal("new goal", onDisk.Items[3].Text);
        Assert.Equal(TodoStore.SourceUser, onDisk.LastModifiedBy);
    }

    [Fact]
    public void AddItem_EmptyText_NoOp()
    {
        new TodoStore(_dir).Save(ThreeItems(), TodoStore.SourceAgent);
        var vm = new TodoViewModel(() => _dir);
        vm.NewItemText = "   ";

        vm.AddItemCommand.Execute(null);

        var onDisk = new TodoStore(_dir).Load();
        Assert.Equal(3, onDisk!.Items.Count);
    }

    [Fact]
    public void RemoveItem_RemovesFromDisk()
    {
        new TodoStore(_dir).Save(ThreeItems(), TodoStore.SourceAgent);
        var vm = new TodoViewModel(() => _dir);

        vm.RemoveItemCommand.Execute(vm.Items[1]);

        var onDisk = new TodoStore(_dir).Load();
        Assert.Equal(2, onDisk!.Items.Count);
        Assert.Equal("goal one", onDisk.Items[0].Text);
        Assert.Equal("goal three", onDisk.Items[1].Text);
        Assert.Equal(TodoStore.SourceUser, onDisk.LastModifiedBy);
    }

    [Fact]
    public void ClearAll_DeletesFile_NoListMeansNoFile()
    {
        new TodoStore(_dir).Save(ThreeItems(), TodoStore.SourceAgent);
        var vm = new TodoViewModel(() => _dir);

        vm.ClearAllCommand.Execute(null);

        Assert.Empty(vm.Items);
        Assert.False(File.Exists(Path.Combine(_dir, TodoStore.FileName)));
    }
}
