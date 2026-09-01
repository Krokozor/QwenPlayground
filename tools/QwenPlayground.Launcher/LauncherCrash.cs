using System.Windows;
using QwenPlayground.Core.Crash;

namespace QwenPlayground.Launcher;

/// <summary>
/// Логгер аварий лаунчера: то же ядро и каталог логов, что у приложения,
/// отдельный канал (logs/launcher-crash-*.log). Лаунчер — единственный, кто
/// может перезапустить/пересобрать приложение: его смерть не должна быть
/// безмолвной, иначе «почему приложение не запускается» останется без ответа.
/// </summary>
public static class LauncherCrash
{
    private static string LogsDir => CrashLogCore.DefaultLogsDir;

    public static string LastCrashFile => CrashLogCore.LastFile(LogsDir, CrashLogCore.LauncherChannel);

    public static void Log(string source, Exception? exception, string? details = null) =>
        CrashLogCore.WriteWithContext(LogsDir, CrashLogCore.LauncherChannel, source, exception, details);

    /// <summary>Полный набор обработчиков для GUI-режима (окно есть — можно и MessageBox).</summary>
    public static void InitializeGuiHandlers(Application application)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain (fatal)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Unobserved task", e.Exception);
            e.SetObserved();
        };
        application.DispatcherUnhandledException += (_, e) =>
        {
            Log("Dispatcher (UI thread)", e.Exception);
            var answer = MessageBox.Show(
                $"Необработанное исключение лаунчера:\n\n{Truncate(e.Exception.ToString(), 1200)}\n\nПолный лог: {LastCrashFile}\n\nПродолжить работу лаунчера?",
                "QwenPlayground Launcher: CRASH",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Error);
            e.Handled = answer == MessageBoxResult.OK;
        };
    }

    /// <summary>
    /// Headless-режим (self-rebuild из приложения): окна нет, диспетчерные
    /// обработчики бессмысленны — достаточно AppDomain + задач.
    /// </summary>
    public static void InitializeHeadlessHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain (fatal)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\n... (обрезано, полный текст в логе)";
}
