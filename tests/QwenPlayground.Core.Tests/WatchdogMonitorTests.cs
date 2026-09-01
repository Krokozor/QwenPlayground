using System.Diagnostics;
using QwenPlayground.Core.Crash;

namespace QwenPlayground.Core.Tests;

public sealed class WatchdogMonitorTests : IDisposable
{
    private readonly string _directory;

    public WatchdogMonitorTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "qwen_watchdog_" + Guid.NewGuid().ToString("N"));
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

    private string Marker() => Path.Combine(_directory, "clean.txt");

    private static Process StartProcess(string arguments)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);
        return process!;
    }

    [Fact]
    public void PollOnce_LiveProcess_ReturnsContinue()
    {
        var self = Process.GetCurrentProcess();
        using (self)
        {
            var monitor = new WatchdogMonitor(self.Id, self.ProcessName);
            Assert.Equal(WatchdogOutcome.Continue, monitor.PollOnce(Marker()));
            Assert.Equal(WatchdogOutcome.Continue, monitor.PollOnce(Marker()));
        }
    }

    [Fact]
    public void PollOnce_TrackedProcessExits_NoMarker_ReturnsDiedWithExitCode()
    {
        var child = StartProcess("/c exit 7");
        using (child)
        {
            var monitor = new WatchdogMonitor(child.Id, "cmd");
            child.WaitForExit(10_000);

            var outcome = monitor.PollOnce(Marker());

            Assert.Equal(WatchdogOutcome.Died, outcome);
            Assert.Equal(7, monitor.ExitCode);
        }
    }

    [Fact]
    public void PollOnce_TrackedProcessExits_FreshMarker_ReturnsCleanExitAndDeletesMarker()
    {
        var child = StartProcess("/c exit 0");
        using (child)
        {
            var monitor = new WatchdogMonitor(child.Id, "cmd");
            child.WaitForExit(10_000);
            File.WriteAllText(Marker(), DateTime.Now.ToString("O"));

            var outcome = monitor.PollOnce(Marker());

            Assert.Equal(WatchdogOutcome.CleanExit, outcome);
            Assert.False(File.Exists(Marker()));
        }
    }

    [Fact]
    public void PollOnce_TrackedProcessExits_StaleMarker_ReturnsDied()
    {
        var child = StartProcess("/c exit 0");
        using (child)
        {
            var monitor = new WatchdogMonitor(child.Id, "cmd");
            child.WaitForExit(10_000);
            var marker = Marker();
            File.WriteAllText(marker, "stale");
            File.SetLastWriteTime(marker, DateTime.Now.AddHours(-2));

            var outcome = monitor.PollOnce(marker);

            Assert.Equal(WatchdogOutcome.Died, outcome);
            // Протухший маркер тоже убираем: он от другого запуска.
            Assert.False(File.Exists(marker));
        }
    }

    [Fact]
    public void PollOnce_ProcessAlreadyDead_ReturnsDied_ExitCodeUnknown()
    {
        // .NET 10: GetProcessById мёртвого процесса не возвращает — захват не удался,
        // код выхода неизвестен, но смерть фиксируется.
        var child = StartProcess("/c exit 0");
        child.WaitForExit(10_000);
        var pid = child.Id; // .NET 10: .Id после Dispose бросает — сохраняем заранее
        child.Dispose();

        var monitor = new WatchdogMonitor(pid, "cmd");
        var outcome = monitor.PollOnce(Marker());

        Assert.Equal(WatchdogOutcome.Died, outcome);
        Assert.Null(monitor.ExitCode);
    }

    [Fact]
    public void PollOnce_WrongProcessName_ReturnsDied()
    {
        // Pid жив, но занят ЧУЖИМ процессом — исходный уже мёртв.
        var child = StartProcess("/c ping -n 30 127.0.0.1");
        using (child)
        {
            var monitor = new WatchdogMonitor(child.Id, "definitely-not-a-real-process-name");
            Assert.Equal(WatchdogOutcome.Died, monitor.PollOnce(Marker()));
        }
    }
}
