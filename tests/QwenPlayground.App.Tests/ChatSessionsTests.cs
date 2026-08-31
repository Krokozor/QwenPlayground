using System.IO;
using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.App.Tests;

/// <summary>
/// Жизненный цикл сессий на изолированном каталоге. Настройки (LastSessionId) — глобальный
/// синглтон: тест сохраняет и восстанавливает значение, чтобы не протечь в соседей.
/// </summary>
public sealed class ChatSessionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qpw_sess_" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedLastSessionId;

    public ChatSessionsTests()
    {
        _savedLastSessionId = AppSettings.Get().LastSessionId;
    }

    [Fact]
    public void EnsureMain_EmptyWorkspace_ReturnsNull_AndListShowsMain()
    {
        var sessions = new ChatSessions(_root);

        Assert.Null(sessions.EnsureMain());

        sessions.RefreshList();
        Assert.Contains(sessions.List, s => s.Id == "main");
        Assert.Equal("main", sessions.CurrentId);
    }

    [Fact]
    public void StartNew_SwitchesCurrent_PersistsLastOpened()
    {
        var sessions = new ChatSessions(_root);

        sessions.StartNew();

        Assert.NotEqual("main", sessions.CurrentId);
        Assert.Equal(sessions.CurrentId, sessions.LastOpenedId);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_MessagesAndCounter()
    {
        var sessions = new ChatSessions(_root);
        sessions.StartNew();
        var log = new ChatLog();
        log.Add(new ChatMessage { Role = ChatRole.User, Content = "вопрос" });
        log.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "ответ" });

        sessions.SaveCurrent(log, log.NextMessageId);

        var fresh = new ChatSessions(_root);
        var data = fresh.Load(sessions.CurrentId);

        Assert.NotNull(data);
        Assert.Equal(2, data!.Messages.Count);
        Assert.Equal("ответ", data.Messages[^1].Content);
        Assert.Equal(log.NextMessageId, data.NextMessageId);

        // Загрузка делает сессию текущей и персистит выбор.
        fresh.Load(data.Id);
        Assert.Equal(data.Id, fresh.CurrentId);
        Assert.Equal(data.Id, fresh.LastOpenedId);
    }

    [Fact]
    public void Load_UnknownId_ReturnsNull_KeepsCurrent()
    {
        var sessions = new ChatSessions(_root);
        sessions.StartNew();
        var before = sessions.CurrentId;

        Assert.Null(sessions.Load("нет-такой"));
        Assert.Equal(before, sessions.CurrentId);
    }

    [Fact]
    public void Delete_CurrentSession_ReturnsTrueAndSwitchesToFresh()
    {
        var sessions = new ChatSessions(_root);
        sessions.StartNew();
        var id = sessions.CurrentId;

        var switched = sessions.Delete(id);

        Assert.True(switched);
        Assert.NotEqual(id, sessions.CurrentId);
        Assert.Null(sessions.Load(id)); // удалена из хранилища
    }

    [Fact]
    public void Delete_OtherSession_ReturnsFalse_CurrentUntouched()
    {
        var sessions = new ChatSessions(_root);
        sessions.StartNew();
        var current = sessions.CurrentId;
        // Обе сессии должны существовать на диске: StartNew только генерирует ID.
        var log = new ChatLog();
        log.Add(new ChatMessage { Role = ChatRole.User, Content = "x" });
        sessions.SaveCurrent(log, log.NextMessageId);

        sessions.StartNew(); // вторая
        var other = sessions.CurrentId;
        sessions.SaveCurrent(log, log.NextMessageId);
        sessions.Load(current); // вернулись на первую

        var switched = sessions.Delete(other);

        Assert.False(switched);
        Assert.Equal(current, sessions.CurrentId);
    }

    [Fact]
    public void RefreshList_GuaranteesMainEntry_EvenIfNeverSaved()
    {
        var sessions = new ChatSessions(_root);
        sessions.RefreshList();

        Assert.Contains(sessions.List, s => s.Id == "main" && s.Title == "★ main-агент");
    }

    public void Dispose()
    {
        AppSettings.Get().LastSessionId = _savedLastSessionId; // не протекаем в другие тесты
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
