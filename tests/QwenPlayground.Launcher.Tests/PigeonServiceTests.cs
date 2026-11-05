using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace QwenPlayground.Launcher.Tests;

public sealed class PigeonServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pigeon-registry-" + Guid.NewGuid().ToString("N"));

    public PigeonServiceTests()
    {
        Directory.CreateDirectory(_root);
        PigeonService.RegistryPathOverride = Path.Combine(_root, "instances.json");
    }

    [Fact]
    public void RefreshInstances_PrunesStaleProcess_KeepsVerifiedProcess()
    {
        using var current = Process.GetCurrentProcess();
        var live = CurrentProcessInstance(current);
        var stale = live with { Pid = -1, WorkspaceRoot = Path.Combine(_root, "other") };
        File.WriteAllText(PigeonService.RegistryPathOverride!, JsonSerializer.Serialize(new[] { live, stale }));

        var instances = PigeonService.RefreshInstances();

        var instance = Assert.Single(instances);
        Assert.Equal(current.Id, instance.Pid);
        Assert.Equal(live.WorkspaceRoot, instance.WorkspaceRoot);
    }

    [Fact]
    public void Stop_ProcessIdentityMismatch_DoesNotKillProcess()
    {
        using var current = Process.GetCurrentProcess();
        var instance = CurrentProcessInstance(current) with
        {
            ExecutablePath = Path.Combine(_root, "not-the-real-process.exe"),
        };
        File.WriteAllText(PigeonService.RegistryPathOverride!, JsonSerializer.Serialize(new[] { instance }));

        var stopped = PigeonService.Stop(current.Id);

        Assert.False(stopped);
        Assert.False(current.HasExited);
        Assert.Empty(PigeonService.RefreshInstances());
    }

    private PigeonInstance CurrentProcessInstance(Process process) => new()
    {
        WorkspaceRoot = _root,
        DataDir = Path.Combine(_root, "data"),
        ExecutablePath = process.MainModule?.FileName ?? throw new InvalidOperationException("process path unavailable"),
        Port = 8199,
        Pid = process.Id,
        ProcessStartTimeUtc = process.StartTime.ToUniversalTime(),
        LaunchedByLauncher = true,
    };

    public void Dispose()
    {
        PigeonService.RegistryPathOverride = null;
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
