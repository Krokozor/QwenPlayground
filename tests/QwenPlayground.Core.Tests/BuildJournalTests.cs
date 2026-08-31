using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Tests;

public sealed class BuildJournalTests : IDisposable
{
    private readonly string _directory;

    public BuildJournalTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "qwen_journal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
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
    public void AppendUpdateAnnounced_RoundTrips()
    {
        BuildJournal.Append(_directory, new BuildJournalEntry
        {
            Id = "b1",
            Timestamp = DateTime.Now,
            BuildExitCode = 0,
            Status = "pending"
        });

        var entries = BuildJournal.Load(_directory);
        Assert.Single(entries);
        Assert.Equal("pending", entries[0].Status);

        BuildJournal.UpdateLast(_directory, "failed", "handshake timeout");
        entries = BuildJournal.Load(_directory);
        Assert.Equal("failed", entries[0].Status);
        Assert.Equal("handshake timeout", entries[0].FailureReason);

        BuildJournal.MarkAnnounced(_directory, new[] { "b1" });
        Assert.True(BuildJournal.Load(_directory)[0].Announced);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(BuildJournal.Load(_directory));
    }

    [Fact]
    public void RestartRequest_ConsumeDeletesFile()
    {
        var file = Path.Combine(_directory, "restart.request");
        SelfBuildService.RequestRestart("test-build-1", file);
        Assert.Equal("test-build-1", SelfBuildService.ConsumeRestartRequest(file));
        Assert.Null(SelfBuildService.ConsumeRestartRequest(file));
    }
}
