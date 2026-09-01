using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.App;

/// <summary>
/// Глобальный логгер аварий приложения. Ловит всё, что способно убить процесс:
/// исключения диспетчера (UI-поток), AppDomain (смертельные, любой поток)
/// и необработанные исключения задач. Пишет через общее <see cref="CrashLogCore"/>
/// в logs/crash-*.log (тот же канал и формат, что у watchdog'а и лаунчера) —
/// одна картина в одном месте, не по кускам.
///
/// Каждая запись несёт контекст: зарегистрированные провайдеры (CrashLog.AddContextProvider)
/// добавляют «что приложение делало в момент смерти» — активные ходы, сессию, FSM.
/// Процесс, который умер мимо managed-обработчиков (нативный краш), фиксирует watchdog.
/// </summary>
public static class CrashLog
{
    private static int _initialized;

    public static string LogsDir => CrashLogCore.DefaultLogsDir;

    public static string LastCrashFile => CrashLogCore.LastFile(LogsDir, CrashLogCore.AppChannel);

    /// <summary>Подключить все глобальные обработчики. Вызвать в конструкторе App.</summary>
    public static void Initialize(Application application)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }
        AppDomain.CurrentDomain.UnhandledException += OnDomainCrash;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        application.DispatcherUnhandledException += OnDispatcherCrash;
    }

    /// <summary>
    /// Зарегистрировать провайдер контекста для записей о крахе
    /// (активные ходы, сессия, состояние FSM — «что делалось в момент смерти»).
    /// </summary>
    public static void AddContextProvider(Func<string> provider) => CrashLogCore.AddContextProvider(provider);

    /// <summary>
    /// Записать «почти-крах»: событие, которое не убивает процесс, но не должно
    /// остаться без следа (смерть рендерера браузера, сбой сохранения при закрытии).
    /// </summary>
    public static void LogCrash(string source, string? details = null, Exception? exception = null)
    {
        CrashLogCore.WriteWithContext(LogsDir, CrashLogCore.AppChannel, source, exception, details);
    }

    /// <summary>
    /// Исключение на UI-потоке: логируем, показываем MessageBox и спрашиваем —
    /// продолжить работу (Handled = true) или умереть (Handled = false).
    /// </summary>
    private static void OnDispatcherCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("Dispatcher (UI thread)", null, e.Exception);
        var answer = MessageBox.Show(
            $"Необработанное UI-исключение:\n\n{Truncate(e.Exception.ToString(), 1200)}\n\nПолный лог: {LastCrashFile}\n\nПродолжить работу приложения?",
            "QwenPlayground: CRASH",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Error);
        e.Handled = answer == MessageBoxResult.OK;
    }

    /// <summary>Смертельное исключение вне диспетчера: процесс всё равно умрёт — фиксируем всё.</summary>
    private static void OnDomainCrash(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown fatal error");
        LogCrash("AppDomain (fatal)",
            e.IsTerminating ? "process is terminating" : null, exception);
        WriteEventLog("AppDomain", exception);
        try
        {
            MessageBox.Show(
                $"ФАТАЛЬНОЕ ИСКЛЮЧЕНИЕ — процесс завершится.\n\n{Truncate(exception.ToString(), 1200)}\n\nПолный лог: {LastCrashFile}",
                "QwenPlayground: FATAL CRASH",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // процесс умирает — диалог не критичен
        }
    }

    /// <summary>
    /// Исключение в забытой задаче (fire-and-forget). Процесс от этого в .NET не умирает,
    /// но молча терять нельзя: пишем в лог и помечаем observed.
    /// </summary>
    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("Unobserved task", null, e.Exception);
        e.SetObserved();
    }

    private static void WriteEventLog(string source, Exception exception)
    {
        try
        {
            const string logName = "Application";
            const string eventSource = "QwenPlayground";
            if (!EventLog.SourceExists(eventSource))
            {
                EventLog.CreateEventSource(eventSource, logName);
            }
            new EventLog(logName, ".", eventSource).WriteEntry(
                $"QwenPlayground crash [{source}]: {exception.Message}",
                EventLogEntryType.Error,
                1001);
        }
        catch
        {
            // нет прав на Event Log — файловый лог основной канал
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\n... (обрезано, полный текст в логе)";
}
