using System.IO;

using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Логика DraftKeeper без таймера: тик сохраняет при изменении и удаляет при переходе
/// в пустое, Flush выгребает текст (не трогая пустое), Restore восстанавливает драфт
/// текущей сессии, ClearOnSend удаляет. Смена сессии — Flush старой + Restore новой,
/// черновики сессий изолированы.
/// </summary>
public sealed class DraftKeeperTests : IDisposable
{
    private sealed class FakeInput
    {
        public string Text { get; set; } = string.Empty;
        public Func<string> Get => () => Text;
        public Action<string> Set => text => Text = text;
    }

    private readonly string _directory;
    private readonly SessionDraftStore _store;
    private readonly FakeInput _input;
    private string _sessionId = "main";
    private readonly DraftKeeper _keeper;

    public DraftKeeperTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "qwen_draftkeeper_" + Guid.NewGuid().ToString("N"));
        _store = new SessionDraftStore(_directory);
        _input = new FakeInput();
        _keeper = new DraftKeeper(_input.Get, _input.Set, () => _sessionId, _store, () => 15);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Tick_TextChanged_SavesDraftForCurrentSession()
    {
        _input.Text = "набрано";
        _keeper.Tick();

        Assert.Equal("набрано", _store.Load("main"));
    }

    [Fact]
    public void Tick_TextUnchanged_DoesNotResave()
    {
        _input.Text = "набрано";
        _keeper.Tick();
        var firstWrite = File.GetLastWriteTime(Path.Combine(_directory, "main", "draft.txt"));

        _keeper.Tick(); // тот же текст
        Assert.Equal(firstWrite, File.GetLastWriteTime(Path.Combine(_directory, "main", "draft.txt")));
    }

    [Fact]
    public void Tick_TransitionToEmpty_ClearsDraft()
    {
        _input.Text = "набрано";
        _keeper.Tick();
        Assert.True(_store.Exists("main"));

        _input.Text = string.Empty; // пользователь очистил
        _keeper.Tick();
        Assert.False(_store.Exists("main"));
    }

    [Fact]
    public void Tick_EmptyStaysEmpty_DoesNotClear()
    {
        // Пустое окошко с самого начала (старт) — драфт не трогаем.
        _input.Text = string.Empty;
        _keeper.Tick();
        Assert.False(_store.Exists("main"));
    }

    [Fact]
    public void Flush_NonEmptyChanged_SavesWithoutClearing()
    {
        _input.Text = "набрано, но тик ещё не бил";
        _keeper.Flush();
        Assert.Equal("набрано, но тик ещё не бил", _store.Load("main"));
    }

    [Fact]
    public void Flush_Empty_DoesNotTouchDraft()
    {
        // Пустой текст при flush (смена сессии/закрытие) не удаляет драфт:
        // пустое окошко — транзитное состояние, а не «пользователь очистил».
        _store.Save("main", "существующий драфт");
        _input.Text = string.Empty;
        _keeper.Flush();
        Assert.Equal("существующий драфт", _store.Load("main"));
    }

    [Fact]
    public void Restore_LoadsCurrentSessionDraftIntoInput()
    {
        _store.Save("main", "восстановленный промпт");
        _input.Text = string.Empty;

        _keeper.Restore();
        Assert.Equal("восстановленный промпт", _input.Text);
    }

    [Fact]
    public void Restore_NoDraft_EmptiesInput()
    {
        _input.Text = "старое";
        _keeper.Restore();
        Assert.Equal(string.Empty, _input.Text);
    }

    [Fact]
    public void ClearOnSend_RemovesDraft()
    {
        _input.Text = "отправляю";
        _keeper.Tick();
        Assert.True(_store.Exists("main"));

        _input.Text = string.Empty; // как в SendAsync
        _keeper.ClearOnSend();
        Assert.False(_store.Exists("main"));
    }

    [Fact]
    public void SessionSwitch_FlushOld_RestoreNew_DraftsIsolated()
    {
        // В main набираем, переключаемся в other: драфт main сохранён, в окошке драфт other.
        _sessionId = "main";
        _input.Text = "черновик main";
        _keeper.Flush(); // как LoadSession: Flush ДО смены
        _sessionId = "other";
        _keeper.Restore(); // Restore ПОСЛЕ смены

        Assert.Equal("черновик main", _store.Load("main"));
        Assert.Equal(string.Empty, _input.Text); // у other драфта не было

        // Возвращаемся в main: его черновик возвращается в окошко.
        _keeper.Flush(); // пустое — не трогаем
        _sessionId = "main";
        _keeper.Restore();
        Assert.Equal("черновик main", _input.Text);
    }
}
