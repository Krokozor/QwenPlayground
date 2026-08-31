namespace QwenPlayground.App;

/// <summary>Сервис с жизненным циклом приложения (аналог IStartable из NekoBot).</summary>
public interface IAppService
{
    string Name { get; }

    /// <summary>Запуск при старте приложения (поток UI).</summary>
    void Start();

    /// <summary>Остановка при закрытии: освободить ресурсы, досохранить состояние.</summary>
    void Shutdown();
}

/// <summary>Адаптер для регистрации лямбд без отдельного класса.</summary>
public sealed class DelegateAppService(string name, Action? start = null, Action? shutdown = null) : IAppService
{
    public string Name => name;

    public void Start() => start?.Invoke();

    public void Shutdown() => shutdown?.Invoke();
}

/// <summary>
/// Централизованный жизненный цикл приложения (аналог реестра IStartable из NekoBot):
/// сервисы регистрируются здесь, MainWindow вызывает StartAll/ShutdownAll. Остановка —
/// в обратном порядке регистрации, каждый сервис в своём try/catch: сбой одного не должен
/// лишить уборки остальных. Раньше закрытие держалось на одном FlushSettingsSave в окне —
/// heartbeat и фоновые работы умирали молча.
/// </summary>
public sealed class AppLifecycle
{
    private readonly List<IAppService> _services = [];
    private readonly Action<string> _reportError;

    public AppLifecycle(Action<string> reportError)
    {
        _reportError = reportError;
    }

    public void Register(IAppService service)
    {
        _services.Add(service);
    }

    public void StartAll()
    {
        foreach (var service in _services)
        {
            RunSafely(service, service.Start, "старт");
        }
    }

    /// <summary>Остановить всё в порядке LIFO и собрать ошибки в отчётчик (без бросков).</summary>
    public List<string> ShutdownAll()
    {
        var failures = new List<string>();
        for (var i = _services.Count - 1; i >= 0; i--)
        {
            var service = _services[i];
            try
            {
                service.Shutdown();
            }
            catch (Exception exception)
            {
                failures.Add($"{service.Name}: {exception.Message}");
                _reportError($"⚠ остановка [{service.Name}]: {exception.Message}");
            }
        }
        return failures;
    }

    private void RunSafely(IAppService service, Action action, string phase)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _reportError($"⚠ {phase} [{service.Name}]: {exception.Message}");
        }
    }
}
