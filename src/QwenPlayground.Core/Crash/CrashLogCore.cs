using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QwenPlayground.Core.Crash;

/// <summary>
/// Ядро аварийного логгирования без WPF: формат записи и запись в файлы.
/// Используют приложение (CrashLog), лаунчер (LauncherCrash) и watchdog —
/// единая картина в одном каталоге logs/:
/// - crash-YYYYMMDD.log / last-crash.log — канал приложения (и watchdog'а,
///   который докладывает о смерти процесса);
/// - launcher-crash-YYYYMMDD.log / last-launcher-crash.log — канал лаунчера.
///
/// Каждая запись самодостаточна: кто, где, версия, OS, исключение, детали
/// и контекст — «что приложение делало в момент смерти» (провайдеры контекста).
/// </summary>
public static class CrashLogCore
{
    public const string AppChannel = "crash";
    public const string LauncherChannel = "launcher-crash";

    private static readonly ConcurrentQueue<Func<string>> ContextProviders = new();
    private static readonly object WriteLock = new();

    /// <summary>Каталог логов приложения/лаунчера: workspace/logs.</summary>
    public static string DefaultLogsDir =>
        Path.Combine(SelfBuild.SelfBuildPaths.WorkspaceRoot, "logs");

    /// <summary>Файл дневного лога канала (создаётся при первой записи).</summary>
    public static string DailyFile(string logsDir, string channel) =>
        Path.Combine(logsDir, $"{channel}-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Файл «последний крах» канала — его первым смотрит человек.</summary>
    public static string LastFile(string logsDir, string channel) =>
        Path.Combine(logsDir, $"last-{channel}.log");

    /// <summary>
    /// Зарегистрировать провайдер контекста: в каждую запись попадает его текст.
    /// Провайдер не должен блокировать (вызов синхронный, в т.ч. из AppDomain-обработчика).
    /// </summary>
    public static void AddContextProvider(Func<string> provider) => ContextProviders.Enqueue(provider);

    /// <summary>Только для тестов: контекст статический, тесты не должны перетекать.</summary>
    public static void ResetContextProvidersForTests()
    {
        while (ContextProviders.TryDequeue(out _))
        {
        }
    }

    /// <summary>Собрать контекст из провайдеров; упавший провайдер не ломает запись.</summary>
    public static string? CollectContext()
    {
        var lines = new List<string>();
        foreach (var provider in ContextProviders)
        {
            try
            {
                var text = provider();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lines.Add(text);
                }
            }
            catch (Exception exception)
            {
                lines.Add($"(context provider failed: {exception.Message})");
            }
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>
    /// Самодостаточная запись: метка времени, процесс, версия, OS, runtime,
    /// детали, исключение, контекст.
    /// </summary>
    public static string BuildEntry(string source, Exception? exception, string? details = null, string? context = null,
        string? processName = null, int? pid = null)
    {
        var now = DateTime.Now;
        string process;
        try
        {
            using var current = Process.GetCurrentProcess();
            // processName/pid — для записи от чужого процесса (watchdog): тогда в
            // «Process» — наблюдаемый процесс, а не сам инструмент записи.
            process = $"{processName ?? current.ProcessName} (PID {pid ?? current.Id})";
        }
        catch
        {
            process = processName is null ? "unknown" : $"{processName} (PID {pid?.ToString() ?? "?"})";
        }

        var builder = new StringBuilder();
        builder.AppendLine("================ CRASH ================");
        builder.AppendLine($"Time: {now:yyyy-MM-dd HH:mm:ss.fff}");
        builder.AppendLine($"Source: {source}");
        builder.AppendLine($"Process: {process}");
        builder.AppendLine($"Version: {SafeVersion()}");
        builder.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        builder.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"Machine: {Environment.MachineName}");
        builder.AppendLine($"User: {Environment.UserName}");
        if (!string.IsNullOrEmpty(details))
        {
            builder.AppendLine("----------------------------------------");
            builder.AppendLine(details);
        }
        if (exception is not null)
        {
            builder.AppendLine("----------------------------------------");
            builder.AppendLine(exception.ToString());
        }
        if (!string.IsNullOrEmpty(context))
        {
            builder.AppendLine("----------------------------------------");
            builder.AppendLine("Context (what was happening):");
            builder.AppendLine(context);
        }
        builder.AppendLine("========================================");
        builder.AppendLine();
        return builder.ToString();
    }

    /// <summary>Записать в дневной файл и «последний крах». Никогда не бросает.</summary>
    public static void Write(string logsDir, string channel, string entry)
    {
        try
        {
            Directory.CreateDirectory(logsDir);
            lock (WriteLock)
            {
                File.AppendAllText(DailyFile(logsDir, channel), entry);
                File.WriteAllText(LastFile(logsDir, channel), entry);
            }
        }
        catch
        {
            // логгер не должен сам бросать исключения
        }
    }

    /// <summary>Удобная запись с авто-контекстом (используют CrashLog и LauncherCrash).</summary>
    public static void WriteWithContext(string logsDir, string channel, string source, Exception? exception,
        string? details = null, string? processName = null, int? pid = null)
    {
        Write(logsDir, channel, BuildEntry(source, exception, details, CollectContext(), processName, pid));
    }

    private static string SafeVersion()
    {
        try
        {
            return typeof(CrashLogCore).Assembly.GetName().Version?.ToString() ?? "?";
        }
        catch
        {
            return "?";
        }
    }
}
