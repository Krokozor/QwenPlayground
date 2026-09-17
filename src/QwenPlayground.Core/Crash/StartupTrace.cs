namespace QwenPlayground.Core.Crash;

/// <summary>
/// Старт-трейс: синхронный построчный лог в logs/startup-YYYYMMDD.log для отладки
/// зависаний на старте. Формат строки: «секунды с запуска процесса, wall-clock,
/// номер потока, сообщение». Синхронная запись, исключения глотаются: трейс сам
/// часть подозреваемой поверхности и не должен ломать старт.
/// </summary>
public static class StartupTrace
{
    private static readonly object Lock = new();
    private static readonly DateTime ProcessStart = DateTime.Now;

    public static void Log(string message)
    {
        try
        {
            var line = $"[{(DateTime.Now - ProcessStart).TotalSeconds:F2}s {DateTime.Now:HH:mm:ss.fff}] [T{Environment.CurrentManagedThreadId}] {message}";
            lock (Lock)
            {
                var dir = CrashLogCore.DefaultLogsDir;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, $"startup-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
        }
        catch
        {
            // трейс не должен ломать старт
        }
    }
}
