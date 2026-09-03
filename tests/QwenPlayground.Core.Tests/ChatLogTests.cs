using QwenPlayground.Core.Chat;

namespace QwenPlayground.Core.Tests;

public sealed class ChatLogTests
{
    private static ChatMessage User(string text) => new() { Role = ChatRole.User, Content = text };
    private static ChatMessage Assistant(string text) => new() { Role = ChatRole.Assistant, Content = text };

    [Fact]
    public void Add_AssignsSequentialIds_SystemIsAlwaysZero()
    {
        var log = new ChatLog();
        log.Add(new ChatMessage { Role = ChatRole.System, Content = "sys" });
        log.Add(User("u1"));
        log.Add(Assistant("a1"));
        log.Add(User("u2"));

        Assert.Equal(0, log[0].Id);
        Assert.Equal(1, log[1].Id);
        Assert.Equal(2, log[2].Id);
        Assert.Equal(3, log[3].Id);
        Assert.Equal(4, log.NextMessageId); // следующий свободный
    }

    [Fact]
    public void Add_ExternalId_BumpsCounter_NeverReuses()
    {
        var log = new ChatLog();
        var external = Assistant("из старой сессии");
        external.Id = 42;
        log.Add(external);

        log.Add(User("новое"));

        Assert.Equal(43, log[1].Id); // счётчик перескочил, а не переиспользовал 1
        Assert.Equal(44, log.NextMessageId);
    }

    [Fact]
    public void SetNextMessageId_OnlyForward()
    {
        var log = new ChatLog();
        log.Add(User("a")); // счётчик → 2
        log.SetNextMessageId(100);
        log.SetNextMessageId(5); // назад нельзя

        Assert.Equal(100, log.NextMessageId);
        log.Add(User("b"));
        Assert.Equal(100, log[1].Id);
    }

    [Fact]
    public void ReplaceAll_AssignsIds_AndFiresSingleChanged()
    {
        var log = new ChatLog();
        log.Add(User("старое")); // счётчик уже на 2
        var changed = 0;
        log.Changed += () => changed++;

        log.ReplaceAll([User("a"), Assistant("b")]);

        Assert.Equal(2, log.Count);
        // Счётчик НЕ откатывается при замене: ID из старого разговора не переиспользуются.
        Assert.Equal(2, log[0].Id);
        Assert.Equal(3, log[1].Id);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void TruncateKeep_RemovesTail_IdsNotReusedAfterCompaction()
    {
        var log = new ChatLog();
        foreach (var i in Enumerable.Range(0, 5))
        {
            log.Add(User($"m{i}")); // ids 1..5
        }
        var changed = 0;
        log.Changed += () => changed++;

        log.TruncateKeep(2);

        Assert.Equal(2, log.Count);
        Assert.Equal(1, changed);
        log.Add(User("после обрезки"));
        Assert.Equal(6, log[^1].Id); // счётчик не откатился
    }

    [Fact]
    public void TrimCompactedPrefix_KeepsSystemAndTail_RemovesMiddle()
    {
        var log = new ChatLog();
        log.Add(new ChatMessage { Role = ChatRole.System, Content = "sys" });
        for (var i = 0; i < 6; i++)
        {
            log.Add(User($"m{i}")); // ids 1..6
        }
        var changed = 0;
        log.Changed += () => changed++;

        // Граница = 4: хвост — m3, m4, m5 (индексы 4..6). Удаляем m0..m2 (индексы 1..3).
        log.TrimCompactedPrefix(4);

        Assert.Equal(4, log.Count);
        Assert.Equal(1, changed);
        Assert.Equal(ChatRole.System, log[0].Role);
        Assert.Equal("m3", log[1].Content);
        Assert.Equal("m4", log[2].Content);
        Assert.Equal("m5", log[^1].Content);
        // ID хвоста сохранились; новые сообщения получают id выше удалённых.
        log.Add(User("новое"));
        Assert.Equal(7, log[^1].Id);
    }

    [Fact]
    public void TrimCompactedPrefix_NoSystem_KeepsTailOnly()
    {
        var log = new ChatLog();
        for (var i = 0; i < 5; i++)
        {
            log.Add(User($"m{i}")); // ids 1..5
        }

        log.TrimCompactedPrefix(3);

        Assert.Equal(2, log.Count);
        Assert.Equal("m3", log[0].Content);
        Assert.Equal("m4", log[^1].Content);
    }

    [Fact]
    public void TrimCompactedPrefix_InvalidBoundary_IsNoOp()
    {
        var log = new ChatLog();
        log.Add(new ChatMessage { Role = ChatRole.System, Content = "sys" });
        log.Add(User("m0"));
        var fired = 0;
        log.Changed += () => fired++;

        log.TrimCompactedPrefix(0);  // <= systemEnd
        log.TrimCompactedPrefix(1);  // == systemEnd
        log.TrimCompactedPrefix(99); // >= count

        Assert.Equal(2, log.Count);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void RemoveFrom_RollsBackTailFromIndexInclusive()
    {
        var log = new ChatLog();
        for (var i = 0; i < 4; i++)
        {
            log.Add(User($"m{i}"));
        }

        log.RemoveFrom(2);

        Assert.Equal(2, log.Count);
        Assert.Equal("m0", log[0].Content);
        Assert.Equal("m1", log[1].Content);
    }

    [Fact]
    public void RemoveFrom_OutOfRange_IsNoOp()
    {
        var log = new ChatLog();
        log.Add(User("a"));
        var fired = 0;
        log.Changed += () => fired++;

        log.RemoveFrom(5);

        Assert.Equal(0, fired);
        Assert.Single(log);
    }

    [Fact]
    public void CopyRange_ReturnsIndependentList_ClampsToCount()
    {
        var log = new ChatLog();
        log.ReplaceAll([User("a"), User("b"), User("c")]);

        var snapshot = log.CopyRange(1, 99);

        Assert.Equal(2, snapshot.Count); // клэмп по границе
        log.TruncateKeep(0);             // лог меняется — снимок независим
        Assert.Equal(2, snapshot.Count);
    }

    [Fact]
    public void Added_FiresWithIdAlreadyAssigned()
    {
        var log = new ChatLog();
        ChatMessage? seen = null;
        log.Added += m => seen = m;

        log.Add(Assistant("ответ"));

        Assert.NotNull(seen);
        Assert.Equal(1, seen!.Id);
    }

    [Fact]
    public void IndexFromEnd_WorksLikeList()
    {
        var log = new ChatLog();
        log.ReplaceAll([User("a"), Assistant("b"), User("c")]);

        Assert.Equal("c", log[^1].Content);
        Assert.Equal("a", log[^3].Content);
    }
}
