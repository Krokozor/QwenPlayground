using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using QwenPlayground.Core.Crash;

namespace QwenPlayground.App;

/// <summary>
/// Детектор зависания старта (баг «после ребилда клиент иногда не запускается»,
/// handshake timeout 09-15/16/18): фоновый поток пингует UI-поток; если тот молчит
/// больше 10 с, пишет в старт-трейс маркер HANG + хвост трейса (последние строки —
/// последний завершённый шаг UI-потока). Точность до 1 с даёт UI-beat-таймер
/// (App.OnStartup). В .NET 10 нет публичного API перечисления managed-потоков
/// (AppDomain.GetThreads удалён, Thread.GetAllThreads отсутствует — проверено),
/// поэтому полный thread dump невозможен; маркер + хвост + beat-таймер дают то же
/// для зоны старта: видно, на каком шаге встал поток.
/// Живёт первые 120 с процесса (зона старта: окно handshake лаунчера 30 с + запас).
/// Пинг — BeginInvoke на Background-приоритете: легитимная тяжёлая работа (пару
/// секунд) пинг пропустит, как только поток освободится; зависание — нет.
/// </summary>
internal static class StartupHangGuard
{
    public static void Start(Dispatcher dispatcher)
    {
        var thread = new Thread(() => Loop(dispatcher))
        {
            IsBackground = true,
            Name = "StartupHangGuard"
        };
        thread.Start();
    }

    private static void Loop(Dispatcher dispatcher)
    {
        var startedAt = DateTime.Now;
        var lastUiSeen = DateTime.Now;
        var lastDump = DateTime.MinValue;
        var process = Process.GetCurrentProcess();
        var lastCpu = process.TotalProcessorTime;
        while (DateTime.Now - startedAt < TimeSpan.FromSeconds(120))
        {
            Thread.Sleep(2000);
            var cpuNow = process.TotalProcessorTime;
            var cpuDelta = cpuNow - lastCpu;
            lastCpu = cpuNow;
            if (Ping(dispatcher, 1000))
            {
                lastUiSeen = DateTime.Now;
                continue;
            }
            var silent = DateTime.Now - lastUiSeen;
            if (silent > TimeSpan.FromSeconds(10) && DateTime.Now - lastDump > TimeSpan.FromSeconds(30))
            {
                lastDump = DateTime.Now;
                // Пинг не проходит, НО процесс жрёт CPU — UI-поток работает (длинная
                // синхронная работа: конструктор окна, 500+ вью сообщений), не висит.
                // CPU на месте — поток заблокирован (лок/файл/ожидание) — настоящее зависание.
                if (cpuDelta > TimeSpan.FromSeconds(1.5))
                {
                    StartupTrace.Log($"BUSY: диспетчер не отвечает {silent.TotalSeconds:F0}s, но CPU +{cpuDelta.TotalSeconds:F1}s за 2с — длинная синхронная работа, не зависание");
                }
                else
                {
                    MarkHang(silent, cpuDelta);
                }
            }
        }
    }

    private static bool Ping(Dispatcher dispatcher, int timeoutMs)
    {
        try
        {
            var done = new ManualResetEventSlim(false);
            dispatcher.BeginInvoke(new Action(done.Set), DispatcherPriority.Background);
            return done.Wait(timeoutMs);
        }
        catch
        {
            // Диспетчер уходит (приложение закрывается) — не зависание.
            return true;
        }
    }

    /// <summary>
    /// Маркер зависания: в старт-трейс (тот же файл, что пишет UI-поток — синхронные
    /// моментальные записи не конфликтуют) — HANG + последние 10 строк трейса:
    /// последний шаг, который UI-поток завершил до того, как встал. Хвост читаем ДО
    /// записи маркера — иначе маркер увидит сам себя.
    /// </summary>
    private static void MarkHang(TimeSpan silent, TimeSpan cpuDelta)
    {
        try
        {
            var file = Path.Combine(CrashLogCore.DefaultLogsDir, $"startup-{DateTime.Now:yyyyMMdd}.log");
            var tail = new List<string>();
            if (File.Exists(file))
            {
                tail = File.ReadAllLines(file).TakeLast(10).ToList();
            }
            StartupTrace.Log($"HANG: диспетчер не отвечает {silent.TotalSeconds:F0}s (до handshake), CPU +{cpuDelta.TotalSeconds:F1}s за 2с — поток заблокирован. Последние строки трейса:");
            foreach (var line in tail)
            {
                StartupTrace.Log("  | " + line);
            }
        }
        catch (Exception exception)
        {
            StartupTrace.Log($"HANG: маркер не удался: {exception.Message}");
        }
    }
}
