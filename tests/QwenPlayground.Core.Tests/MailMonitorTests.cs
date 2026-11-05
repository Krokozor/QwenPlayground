using MailKit;
using QwenPlayground.Core.Mail;

namespace QwenPlayground.Core.Tests;

/// <summary>Монитор тестируется через fake IMAP: без сети и без изменения живых настроек.</summary>
public sealed class MailMonitorTests : IDisposable
{
    public MailMonitorTests() => MailMonitor.Stop();
    public void Dispose() => MailMonitor.Stop();

    [Fact]
    public async Task PollOnce_Uses_TotalCount_Not_PageSize()
    {
        var connection = new FakeConnection(Result(17, 10));
        var monitor = new MailMonitor(() => connection, () => new MailSettings());

        await monitor.PollOnceAsync();

        Assert.Equal(17, monitor.CurrentUnreadCount);
        Assert.Equal("subject-10", monitor.CurrentLatest?.Subject);
    }

    [Fact]
    public async Task SameUid_DoesNotWakeTwice()
    {
        var wakes = new List<string>();
        MailMonitor.RegisterMain(() => false, wakes.Add);
        var connection = new FakeConnection(Result(1, 10), Result(1, 10));
        var monitor = new MailMonitor(() => connection, () => new MailSettings());

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Single(wakes);
    }

    [Fact]
    public async Task NewUid_Wakes_EvenAfterUnreadCountDrops()
    {
        var wakes = new List<string>();
        MailMonitor.RegisterMain(() => false, wakes.Add);
        var connection = new FakeConnection(Result(5, 10), Result(2, 11));
        var monitor = new MailMonitor(() => connection, () => new MailSettings());

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(2, wakes.Count);
        Assert.Contains("2 unread total", wakes[1]);
    }

    [Fact]
    public async Task BusyChat_DefersWake_UntilIdle()
    {
        var busy = true;
        var wakes = new List<string>();
        MailMonitor.RegisterMain(() => busy, wakes.Add);
        var connection = new FakeConnection(Result(1, 10));
        var monitor = new MailMonitor(() => connection, () => new MailSettings());

        await monitor.PollOnceAsync();
        Assert.Empty(wakes);
        busy = false;
        await monitor.PollOnceAsync();
        Assert.Single(wakes);
    }

    [Fact]
    public async Task SenderPolicy_OnlyWakesForMatchingSender()
    {
        var wakes = new List<string>();
        MailMonitor.RegisterMain(() => false, wakes.Add);
        var settings = new MailSettings
        {
            WakePolicy = MailWakePolicy.Senders,
            WakeSenders = ["owner@example.com"]
        };
        var connection = new FakeConnection(
            Result(1, 10, "someone@example.com"),
            Result(2, 11, "Owner <owner@example.com>"));
        var monitor = new MailMonitor(() => connection, () => settings);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Single(wakes);
    }

    [Fact]
    public async Task FolderPolicy_RequiresInboxInConfiguredFolders()
    {
        var wakes = new List<string>();
        MailMonitor.RegisterMain(() => false, wakes.Add);
        var settings = new MailSettings { WakePolicy = MailWakePolicy.Folders };
        var connection = new FakeConnection(Result(1, 10));
        var monitor = new MailMonitor(() => connection, () => settings);

        await monitor.PollOnceAsync();
        Assert.Empty(wakes);
        settings.WakeFolders.Add("INBOX");
        await monitor.PollOnceAsync();
        Assert.Single(wakes);
    }

    [Fact]
    public async Task DroppedConnection_IsRecreated_AndOldOneDisposed()
    {
        var first = new FakeConnection(Result(1, 10));
        var second = new FakeConnection(Result(1, 10));
        var created = 0;
        var monitor = new MailMonitor(() => ++created == 1 ? first : second, () => new MailSettings());

        await monitor.PollOnceAsync();
        first.IsConnected = false;
        await monitor.PollOnceAsync();

        Assert.Equal(2, created);
        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
    }

    [Fact]
    public void TryStart_IsIdempotent_AndStopDisposesService()
    {
        var connection = new FakeConnection(Result(0));
        Assert.True(MailMonitor.TryStart(() => connection, () => new MailSettings()));
        Assert.False(MailMonitor.TryStart(() => connection, () => new MailSettings()));

        MailMonitor.Stop();

        Assert.Null(MailMonitor.Instance);
        Assert.True(connection.Disposed);
    }

    private static MailUnreadResult Result(int total, uint uid = 0, string from = "sender@example.com") =>
        new(total, uid == 0
            ? []
            : [new MailMessage(new UniqueId(uid), $"subject-{uid}", from, DateTimeOffset.UnixEpoch, "snippet")]);

    private sealed class FakeConnection(params MailUnreadResult[] results) : IMailConnection
    {
        private readonly Queue<MailUnreadResult> _results = new(results);
        private MailUnreadResult _last = new(0, []);

        public bool IsConnected { get; set; }
        public bool Disposed { get; private set; }

        public Task ConnectAsync(CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task<MailUnreadResult> GetUnreadAsync(string folderName, int max = 20, CancellationToken ct = default)
        {
            if (_results.Count > 0) _last = _results.Dequeue();
            return Task.FromResult(_last);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
