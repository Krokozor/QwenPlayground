using QwenPlayground.Core.Heartbeat;

namespace QwenPlayground.Core.Tests;

public sealed class WakeSignalStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qpw-wake-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Empty_TakeNext_ReturnsNull()
    {
        var store = new WakeSignalStore(_directory);

        Assert.Null(store.TakeNext());
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Send_TakeNext_RoundTripAndDeletesFile()
    {
        var store = new WakeSignalStore(_directory);
        store.Send("  check the tests  ");

        var signal = store.TakeNext();

        Assert.NotNull(signal);
        Assert.Equal("check the tests", signal!.Value.Text);
        Assert.NotEqual(string.Empty, signal.Value.Source);
        Assert.Null(store.TakeNext());
    }

    [Fact]
    public void TakeNext_ReturnsOldestFirst_ByFileName()
    {
        var store = new WakeSignalStore(_directory);
        File.WriteAllText(Path.Combine(_directory, "20260816-100000-000.txt"), "first");
        File.WriteAllText(Path.Combine(_directory, "20260816-100001-000.txt"), "second");

        Assert.Equal("first", store.TakeNext()!.Value.Text);
        Assert.Equal("second", store.TakeNext()!.Value.Text);
        Assert.Null(store.TakeNext());
    }

    [Fact]
    public void Send_TwiceInSameMillisecond_BothSurvive()
    {
        var store = new WakeSignalStore(_directory);
        store.Send("a");
        store.Send("b");

        Assert.Equal(2, store.Count);
        var first = store.TakeNext()!.Value.Text;
        var second = store.TakeNext()!.Value.Text;

        Assert.Equal(new HashSet<string> { "a", "b" }, new HashSet<string> { first, second });
    }
}
