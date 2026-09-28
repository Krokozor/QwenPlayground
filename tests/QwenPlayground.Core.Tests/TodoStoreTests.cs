using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// TODO-список сессии (TODO.json): round-trip, «нет файла — нет списка», удаление пустого,
/// контентный хэш (порядок/текст/done) и живое событие Saved.
/// </summary>
public sealed class TodoStoreTests : IDisposable
{
    private readonly string _dir;

    public TodoStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qwen_todo_tests_" + Guid.NewGuid().ToString("N"));
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

    private TodoStore Store() => new(_dir);

    [Fact]
    public void Load_NoFile_ReturnsNull()
    {
        Assert.Null(Store().Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var list = new TodoList
        {
            Items =
            {
                new TodoItem { Text = "fix the bug", Done = false },
                new TodoItem { Text = "add tests", Done = true }
            }
        };
        Store().Save(list, TodoStore.SourceUser);

        var loaded = Store().Load();
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Items.Count);
        Assert.Equal("fix the bug", loaded.Items[0].Text);
        Assert.False(loaded.Items[0].Done);
        Assert.Equal("add tests", loaded.Items[1].Text);
        Assert.True(loaded.Items[1].Done);
        Assert.Equal(TodoStore.SourceUser, loaded.LastModifiedBy);
    }

    [Fact]
    public void Save_EmptyList_DeletesFile_NoListMeansNoFile()
    {
        var list = new TodoList { Items = { new TodoItem { Text = "goal" } } };
        Store().Save(list, TodoStore.SourceAgent);
        Assert.True(File.Exists(Store().FilePath));

        list.Items.Clear();
        Store().Save(list, TodoStore.SourceUser);

        Assert.False(File.Exists(Store().FilePath));
        Assert.Null(Store().Load());
    }

    [Fact]
    public void Save_SetsSourceAndSkipsBlankItems()
    {
        var list = new TodoList
        {
            Items = { new TodoItem { Text = "  " }, new TodoItem { Text = "real goal" } }
        };
        Store().Save(list, TodoStore.SourceAgent);

        var loaded = Store().Load();
        Assert.NotNull(loaded);
        var item = Assert.Single(loaded!.Items);
        Assert.Equal("real goal", item.Text);
        Assert.Equal(TodoStore.SourceAgent, loaded.LastModifiedBy);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull_DoesNotThrow()
    {
        File.WriteAllText(Store().FilePath, "{ это не json");
        Assert.Null(Store().Load());
    }

    [Fact]
    public void ContentHash_ChangesWithTextDoneAndOrder()
    {
        var baseList = new TodoList { Items = { new TodoItem { Text = "a" }, new TodoItem { Text = "b" } } };
        var hash = TodoStore.ContentHash(baseList);

        var differentText = new TodoList { Items = { new TodoItem { Text = "a" }, new TodoItem { Text = "B" } } };
        Assert.NotEqual(hash, TodoStore.ContentHash(differentText));

        var differentDone = new TodoList { Items = { new TodoItem { Text = "a", Done = true }, new TodoItem { Text = "b" } } };
        Assert.NotEqual(hash, TodoStore.ContentHash(differentDone));

        var differentOrder = new TodoList { Items = { new TodoItem { Text = "b" }, new TodoItem { Text = "a" } } };
        Assert.NotEqual(hash, TodoStore.ContentHash(differentOrder));

        // Метаданные (источник/время) не влияют на отпечаток.
        var sameContent = new TodoList
        {
            Items = { new TodoItem { Text = "a" }, new TodoItem { Text = "b" } },
            LastModifiedBy = TodoStore.SourceUser,
            UpdatedAt = DateTime.Now
        };
        Assert.Equal(hash, TodoStore.ContentHash(sameContent));
    }

    [Fact]
    public void Save_RaisesSavedEvent_WithSessionDir()
    {
        string? observed = null;
        void Handler(string dir) => observed = dir;
        TodoStore.Saved += Handler;
        try
        {
            Store().Save(new TodoList { Items = { new TodoItem { Text = "goal" } } }, TodoStore.SourceAgent);
        }
        finally
        {
            TodoStore.Saved -= Handler; // событие статическое — не оставляем подписку между тестами
        }
        Assert.Equal(_dir, observed);
    }
}
