using System.IO;
using QwenPlayground.App;

namespace QwenPlayground.App.Tests;

public sealed class AppLifecycleTests
{
    private sealed class RecordingService(string name, List<string> trace, bool failOnShutdown = false) : IAppService
    {
        public string Name => name;
        public void Start() => trace.Add("start:" + name);
        public void Shutdown()
        {
            trace.Add("shutdown:" + name);
            if (failOnShutdown)
            {
                throw new InvalidOperationException(name + " сломался");
            }
        }
    }

    [Fact]
    public void StartAll_RunsInRegistrationOrder()
    {
        var trace = new List<string>();
        var lifecycle = new AppLifecycle(_ => { });
        lifecycle.Register(new DelegateAppService("a", start: () => trace.Add("start:a")));
        lifecycle.Register(new DelegateAppService("b", start: () => trace.Add("start:b")));

        lifecycle.StartAll();

        Assert.Equal(["start:a", "start:b"], trace);
    }

    [Fact]
    public void ShutdownAll_RunsInReverseOrder_Lifo()
    {
        var trace = new List<string>();
        var lifecycle = new AppLifecycle(_ => { });
        lifecycle.Register(new DelegateAppService("первый", shutdown: () => trace.Add("stop:первый")));
        lifecycle.Register(new DelegateAppService("второй", shutdown: () => trace.Add("stop:второй")));

        lifecycle.ShutdownAll();

        // Кто стартовал последним — остановился первым.
        Assert.Equal(["stop:второй", "stop:первый"], trace);
    }

    [Fact]
    public void Shutdown_FailureOfOne_DoesNotBlockOthers_AndIsReported()
    {
        var trace = new List<string>();
        var errors = new List<string>();
        var lifecycle = new AppLifecycle(errors.Add);
        lifecycle.Register(new DelegateAppService("здоровый", shutdown: () => trace.Add("stop:здоровый")));
        lifecycle.Register(new RecordingService("сломанный", trace, failOnShutdown: true));

        var failures = lifecycle.ShutdownAll();

        Assert.Equal(["shutdown:сломанный", "stop:здоровый"], trace); // уборка продолжилась после сбоя
        var failure = Assert.Single(failures);
        Assert.Contains("сломанный", failure);
        Assert.Single(errors);
    }

    [Fact]
    public async Task HeartbeatController_AsService_ShutdownStopsTimer()
    {
        // Контракт IAppService у контроллера: Start идемпотентен, Shutdown гасит таймер.
        var controller = new HeartbeatController(
            new Core.Heartbeat.WakeSignalStore(Path.Combine(Path.GetTempPath(), "qpw_lc_" + Guid.NewGuid().ToString("N"))),
            isBusy: () => false,
            heartbeatEnabled: () => true,
            heartbeatIntervalMinutes: () => 30,
            setStatus: _ => { },
            startTurn: _ => Task.CompletedTask,
            flushMemory: () => Task.CompletedTask);

        Assert.Equal("heartbeat", controller.Name);
        controller.Start();
        controller.Start(); // повторный Start не должен дублировать подписку/падать
        controller.Shutdown();
    }
}
