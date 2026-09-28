namespace QwenPlayground.Core.Crash;

/// <summary>
/// Журнал событий приложения: ВСЕГДА включённый лог важных событий состояния, которые
/// обязаны быть видны «после 8 часов» (сбои записи файлов, неатомарный fallback,
/// пропуски KV-якоря). Не путать с System.Diagnostics.EventLog (журнал Windows) и с
/// <see cref="EventLogExcerpt"/> (его читатель).
///
/// В отличие от <see cref="DiagnosticsLog"/> — НЕ гейтится DiagnosticsMode: принцип
/// «деградация обязана быть громкой» (кодревью 2026-09-28) — тихий сбой в нормальном
/// режиме незаметен до возвращения владельца, а это худший исход для harness'а.
///
/// Формат: строка на событие в logs/events-YYYYMMDD.log (несколько строк в день,
/// файл остаётся маленьким). Никогда не бросает: логгер не должен ломать тот код,
/// который фиксирует.
/// </summary>
public static class AppEventLog
{
    private static readonly object Lock = new();
    private static readonly DateTime ProcessStart = DateTime.Now;

    /// <summary>Записать строку в events-лог. Никогда не бросает.</summary>
    public static void Log(string message)
    {
        try
        {
            var now = DateTime.Now;
            var line = $"[{(now - ProcessStart).TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}s {now:yyyy-MM-dd HH:mm:ss}] {message}";
            lock (Lock)
            {
                var directory = Path.Combine(SelfBuild.SelfBuildPaths.WorkspaceRoot, "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"events-{now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
        }
        catch
        {
            // events-лог не должен ломать фиксируемый код
        }
    }
}
