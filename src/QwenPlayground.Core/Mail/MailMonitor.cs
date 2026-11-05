using QwenPlayground.Core.MetaInfo;

namespace QwenPlayground.Core.Mail;

/// <summary>
/// Монитор входящей почты (процессный, один на всё приложение). Poll-цикл на UI-контексте
/// (инвариант: агентный код на UI-потоке, как PigeonSession.PollLoopAsync): каждые N секунд
/// проверяет непрочитанные в INBOX, обновляет состояние для state-строки, будит main-окно
/// по политике WakePolicy (если чат idle).
/// </summary>
public sealed class MailMonitor
{
    private static readonly object Gate = new();
    private static MailMonitor? _instance;
    private static CancellationTokenSource? _cts;
    private static Task? _pollLoop;
    private static Func<bool>? _registeredMainBusy;
    private static Action<string>? _registeredMainWake;

    private readonly Func<IMailConnection> _serviceFactory;
    private readonly Func<MailSettings> _settings;
    private readonly HashSet<string> _wokenUids = new(StringComparer.Ordinal);
    private IMailConnection? _service;
    private int _unreadCount;
    private (string From, string Subject, DateTimeOffset Ts)? _latest;
    private string? _lastError;
    private Func<bool>? _mainBusy;
    private Action<string>? _mainWake;

    private MailMonitor()
        : this(() => new MailService(), () => MailSettings.Get())
    {
    }

    internal MailMonitor(Func<IMailConnection> serviceFactory, Func<MailSettings> settings)
    {
        _serviceFactory = serviceFactory;
        _settings = settings;
        _mainBusy = _registeredMainBusy;
        _mainWake = _registeredMainWake;
    }

    /// <summary>Живой монитор (null, если не запущен).</summary>
    public static MailMonitor? Instance { get { lock (Gate) return _instance; } }

    internal int CurrentUnreadCount => _unreadCount;
    internal (string From, string Subject, DateTimeOffset Ts)? CurrentLatest => _latest;

    /// <summary>Зависимости main-окна сохраняются и для монитора, запущенного позже из настроек.</summary>
    public static void RegisterMain(Func<bool> isBusy, Action<string> wake)
    {
        lock (Gate)
        {
            _registeredMainBusy = isBusy;
            _registeredMainWake = wake;
            if (_instance is not null)
            {
                _instance._mainBusy = isBusy;
                _instance._mainWake = wake;
            }
        }
    }

    /// <summary>Запустить монитор (идемпотентно). Poll-цикл на UI-контексте (без Task.Run).</summary>
    public static bool TryStart() => TryStart(() => new MailService(), () => MailSettings.Get());

    internal static bool TryStart(Func<IMailConnection> serviceFactory, Func<MailSettings> settings)
    {
        lock (Gate)
        {
            if (_instance is not null) return false;
            _instance = new MailMonitor(serviceFactory, settings);
            _cts = new CancellationTokenSource();
            _pollLoop = _instance.PollLoopAsync(_cts.Token);
            return true;
        }
    }

    /// <summary>Остановить цикл и закрыть IMAP-соединение.</summary>
    public static void Stop()
    {
        MailMonitor? monitor;
        CancellationTokenSource? cts;
        Task? loop;
        lock (Gate)
        {
            monitor = _instance;
            cts = _cts;
            loop = _pollLoop;
            _instance = null;
            _cts = null;
            _pollLoop = null;
        }

        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        if (loop is not null)
        {
            try { loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        }
        if (monitor is not null)
        {
            try { monitor.DisposeServiceAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
        cts?.Dispose();
    }

    /// <summary>Непрочитанных в INBOX (для state-строки).</summary>
    public static int UnreadCount { get { var m = Instance; return m?._unreadCount ?? 0; } }

    /// <summary>Последнее письмо (для state-строки).</summary>
    public static (string From, string Subject, DateTimeOffset Ts)? Latest
    {
        get { var m = Instance; return m?._latest; }
    }

    /// <summary>Последняя ошибка polling (для state-строки/диагностики).</summary>
    public static string? LastError { get { var m = Instance; return m?._lastError; } }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
            try
            {
                var interval = _settings().PollIntervalSeconds;
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, interval)), ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct = default)
    {
        var service = await GetOrCreateServiceAsync(ct);
        var result = await service.GetUnreadAsync("INBOX", max: 1, ct);
        _unreadCount = result.TotalCount;
        var latest = result.Messages.Count > 0 ? result.Messages[^1] : null;
        _latest = latest is null ? null : (latest.From, latest.Subject, latest.Date);
        _lastError = null;
        TryWakeIfIdle(latest);
    }

    private async Task<IMailConnection> GetOrCreateServiceAsync(CancellationToken ct)
    {
        var current = _service;
        if (current is { IsConnected: true }) return current;
        var service = _serviceFactory();
        await service.ConnectAsync(ct);
        _service = service;
        if (current is not null) await current.DisposeAsync();
        return service;
    }

    private async ValueTask DisposeServiceAsync()
    {
        if (_service is not null)
        {
            await _service.DisposeAsync();
            _service = null;
        }
    }

    private void TryWakeIfIdle(MailMessage? latest)
    {
        if (_mainBusy is null || _mainWake is null || latest is null) return;
        if (_mainBusy()) return;

        var uid = latest.Uid.ToString();
        if (_wokenUids.Contains(uid)) return;

        var settings = _settings();
        var shouldWake = settings.WakePolicy switch
        {
            MailWakePolicy.All => true,
            MailWakePolicy.Folders => settings.WakeFolders.Contains("INBOX", StringComparer.OrdinalIgnoreCase),
            MailWakePolicy.Senders => settings.WakeSenders
                .Any(s => s.Length > 0 && latest.From.Contains(s, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
        if (!shouldWake) return;

        _wokenUids.Add(uid);
        _mainWake(
            $"[mail] New mail: {latest.From} \"{latest.Subject}\" " +
            $"({_unreadCount} unread total). Use mail_check to see details, mail_read to read.");
    }
}

/// <summary>
/// Почтовый анонсер state-блока: строка «mail: N unread (latest …)» на момент рендера.
/// Не настроено/не запущено — тишина (пустой список).
/// </summary>
public sealed class MailStateAnnouncer : IStateAnnouncer
{
    public IReadOnlyList<StateAnnounce> GetAnnounces()
    {
        if (!MailSettings.Configured) return [];
        if (MailMonitor.Instance is null) return [];

        var messages = new List<string>(1);
        var line = MailMonitor.UnreadCount > 0
            ? $"inbox {MailMonitor.UnreadCount} unread"
            : "inbox 0 unread";
        if (MailMonitor.Latest is { } latest)
        {
            line += $" (latest {latest.Ts.ToLocalTime():HH:mm}: \"{Truncate(latest.Subject)}\" from {Truncate(latest.From, 60)})";
        }
        if (MailMonitor.LastError is { Length: > 0 } err && MailMonitor.UnreadCount == 0)
        {
            line += $"; poll error: {Truncate(err, 80)}";
        }
        messages.Add(line);
        return [new StateAnnounce { Label = "mail", Messages = messages }];
    }

    private static string Truncate(string s, int max = 120)
    {
        var one = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }
}
