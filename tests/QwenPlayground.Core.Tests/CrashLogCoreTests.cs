using QwenPlayground.Core.Crash;

namespace QwenPlayground.Core.Tests;

public sealed class CrashLogCoreTests : IDisposable
{
    private readonly string _directory;

    public CrashLogCoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "qwen_crashlog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CrashLogCore.ResetContextProvidersForTests();
    }

    public void Dispose()
    {
        CrashLogCore.ResetContextProvidersForTests();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void BuildEntry_ContainsSourceExceptionAndDetails()
    {
        var exception = new InvalidOperationException("boom");
        var entry = CrashLogCore.BuildEntry("Test source", exception, details: "some details");

        Assert.Contains("Source: Test source", entry);
        Assert.Contains("some details", entry);
        Assert.Contains("boom", entry);
        Assert.Contains("InvalidOperationException", entry);
    }

    [Fact]
    public void BuildEntry_WithContext_ContainsContextBlock()
    {
        var entry = CrashLogCore.BuildEntry("src", null, context: "session: main\nactive turns: none");
        Assert.Contains("Context (what was happening):", entry);
        Assert.Contains("session: main", entry);
    }

    [Fact]
    public void CollectContext_ProviderFails_DoesNotBreakOthers()
    {
        CrashLogCore.AddContextProvider(() => "good context");
        CrashLogCore.AddContextProvider(() => throw new InvalidOperationException("provider boom"));

        var context = CrashLogCore.CollectContext();

        Assert.Contains("good context", context);
        Assert.Contains("provider boom", context);
    }

    [Fact]
    public void CollectContext_NoProviders_ReturnsNull()
    {
        Assert.Null(CrashLogCore.CollectContext());
    }

    [Fact]
    public void Write_CreatesDailyAndLastFiles_SecondWriteAppends()
    {
        CrashLogCore.Write(_directory, CrashLogCore.AppChannel, CrashLogCore.BuildEntry("first", null));
        CrashLogCore.Write(_directory, CrashLogCore.AppChannel, CrashLogCore.BuildEntry("second", null));

        var daily = CrashLogCore.DailyFile(_directory, CrashLogCore.AppChannel);
        var last = CrashLogCore.LastFile(_directory, CrashLogCore.AppChannel);
        var dailyText = File.ReadAllText(daily);
        var lastText = File.ReadAllText(last);

        Assert.Contains("Source: first", dailyText);
        Assert.Contains("Source: second", dailyText);
        // «Последний крах» — только последняя запись.
        Assert.DoesNotContain("Source: first", lastText);
        Assert.Contains("Source: second", lastText);
    }

    [Fact]
    public void WriteChannels_DonNotMix()
    {
        CrashLogCore.Write(_directory, CrashLogCore.AppChannel, CrashLogCore.BuildEntry("app crash", null));
        CrashLogCore.Write(_directory, CrashLogCore.LauncherChannel, CrashLogCore.BuildEntry("launcher crash", null));

        var appLast = File.ReadAllText(CrashLogCore.LastFile(_directory, CrashLogCore.AppChannel));
        var launcherLast = File.ReadAllText(CrashLogCore.LastFile(_directory, CrashLogCore.LauncherChannel));

        Assert.Contains("app crash", appLast);
        Assert.DoesNotContain("launcher crash", appLast);
        Assert.Contains("launcher crash", launcherLast);
        Assert.DoesNotContain("app crash", launcherLast);
    }

    [Fact]
    public void Write_NeverThrows_OnBadPath()
    {
        // Невозможный путь (каталог — файл): логгер не должен бросать.
        var file = Path.Combine(_directory, "blocker");
        File.WriteAllText(file, "x");
        var logsDir = Path.Combine(file, "impossible");
        CrashLogCore.Write(logsDir, CrashLogCore.AppChannel, "entry");
    }

    [Fact]
    public void SplitEntries_ReturnsSelfContainedEntriesWithHeader()
    {
        var text = CrashLogCore.BuildEntry("first", null) + CrashLogCore.BuildEntry("second", null);
        var entries = CrashLogCore.SplitEntries(text).ToList();

        Assert.Equal(2, entries.Count);
        Assert.StartsWith(CrashLogCore.EntryHeader, entries[0]);
        Assert.Contains("Source: first", entries[0]);
        Assert.DoesNotContain("Source: second", entries[0]);
        Assert.Contains("Source: second", entries[1]);
    }

    [Fact]
    public void SplitEntries_EmptyOrHeaderlessText_ReturnsNothing()
    {
        Assert.Empty(CrashLogCore.SplitEntries(""));
        Assert.Empty(CrashLogCore.SplitEntries("no entries here"));
    }

    [Fact]
    public void ReadEntries_DeduplicatesLastFileAgainstDaily()
    {
        // last-файл дублирует последнюю запись дневного — в результате она одна.
        CrashLogCore.Write(_directory, CrashLogCore.AppChannel, CrashLogCore.BuildEntry("first", null));
        CrashLogCore.Write(_directory, CrashLogCore.AppChannel, CrashLogCore.BuildEntry("second", null));

        var entries = CrashLogCore.ReadEntries(_directory, CrashLogCore.AppChannel);

        Assert.Equal(2, entries.Count);
        Assert.Contains("Source: first", entries[0]);
        Assert.Contains("Source: second", entries[1]);
    }

    [Fact]
    public void ReadEntries_RespectsMax_ReturnsMostRecent()
    {
        for (var i = 1; i <= 5; i++)
        {
            CrashLogCore.Write(_directory, CrashLogCore.AppChannel, CrashLogCore.BuildEntry($"crash{i}", null));
        }

        var entries = CrashLogCore.ReadEntries(_directory, CrashLogCore.AppChannel, max: 2);

        Assert.Equal(2, entries.Count);
        Assert.Contains("Source: crash4", entries[0]);
        Assert.Contains("Source: crash5", entries[1]);
    }

    [Fact]
    public void ReadEntries_MissingFiles_ReturnsEmptyList()
    {
        Assert.Empty(CrashLogCore.ReadEntries(Path.Combine(_directory, "nope"), CrashLogCore.AppChannel));
    }
}
