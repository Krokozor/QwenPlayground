using QwenPlayground.Core.Crash;

namespace QwenPlayground.Core.Tests;

public sealed class DiagnosticsLogTests : IDisposable
{
    private readonly string _directory;

    public DiagnosticsLogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "qwen_diaglog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        DiagnosticsLog.ResetForTests();
        DiagnosticsLog.SetLogsDirForTests(_directory);
    }

    public void Dispose()
    {
        DiagnosticsLog.ResetForTests();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Log_Disabled_DoesNotWrite()
    {
        DiagnosticsLog.SetEnabled(false);
        DiagnosticsLog.Log("should not appear");

        var file = DiagnosticsLog.DailyFile(_directory);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Log_Enabled_WritesDailyFile()
    {
        DiagnosticsLog.SetEnabled(true);
        DiagnosticsLog.Log("hello diag");

        var file = DiagnosticsLog.DailyFile(_directory);
        Assert.True(File.Exists(file));
        var text = File.ReadAllText(file);
        Assert.Contains("hello diag", text);
    }

    [Fact]
    public void Log_Enabled_SecondWriteAppends()
    {
        DiagnosticsLog.SetEnabled(true);
        DiagnosticsLog.Log("first");
        DiagnosticsLog.Log("second");

        var file = DiagnosticsLog.DailyFile(_directory);
        var text = File.ReadAllText(file);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
        // second после first (append, не overwrite).
        Assert.True(text.IndexOf("second", StringComparison.Ordinal) > text.IndexOf("first", StringComparison.Ordinal));
    }

    [Fact]
    public void Log_Enabled_LineHasTimestampAndThread()
    {
        DiagnosticsLog.SetEnabled(true);
        DiagnosticsLog.Log("format check");

        var file = DiagnosticsLog.DailyFile(_directory);
        var line = File.ReadAllText(file).Split('\n').First(l => l.Contains("format check")).TrimEnd('\r');
        // Формат: [+N.NNs HH:mm:ss.fff] [T12] message
        Assert.Matches(@"^\[\d+\.\d{2}s \d{2}:\d{2}:\d{2}\.\d{3}\] \[T\d+\] format check$", line);
    }

    [Fact]
    public void Log_Enabled_ThenDisabled_StopsWriting()
    {
        DiagnosticsLog.SetEnabled(true);
        DiagnosticsLog.Log("on");
        DiagnosticsLog.SetEnabled(false);
        DiagnosticsLog.Log("off");

        var file = DiagnosticsLog.DailyFile(_directory);
        var text = File.ReadAllText(file);
        Assert.Contains("on", text);
        Assert.DoesNotContain("off", text);
    }

    [Fact]
    public void SetEnabled_OverridesLazySettingsRead()
    {
        // Даже если настройки не читались, SetEnabled(true) включает режим.
        DiagnosticsLog.SetEnabled(true);
        Assert.True(DiagnosticsLog.Enabled);
        DiagnosticsLog.SetEnabled(false);
        Assert.False(DiagnosticsLog.Enabled);
    }
}
