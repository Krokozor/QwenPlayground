using QwenPlayground.Core.Crash;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Inference;

/// <summary>
/// Трафик-лог: полный промпт+вывод каждой итерации каждого агента
/// (logs/traffic-YYYYMMDD.log). Самый объёмный лог приложения — поэтому гейтится
/// настройкой (<see cref="AppSettings.TrafficLogEnabled"/>) и потолком дневного файла
/// (<see cref="MaxDailyBytes"/>): без них за несколько суток это десятки ГБ.
///
/// Ошибки записи и выход за потолок — в events-лог (с троттлингом): глухой catch —
/// тихая деградация, диск-проблема должна быть видна «после 8 часов».
/// </summary>
public static class TrafficLog
{
    private static readonly object Lock = new();

    /// <summary>Потолок дневного файла: дальше запись на сегодня останавливается (заметка в events-лог).</summary>
    public const long MaxDailyBytes = 500L * 1024 * 1024;

    // Троттлинг заметок: сбой диска не должен сам стать лог-флудом.
    private static DateTime _lastErrorNote = DateTime.MinValue;
    private static DateTime _lastCapNote = DateTime.MinValue;

    public static void Log(string prompt, string output)
    {
        if (!AppSettings.Get().TrafficLogEnabled)
        {
            return;
        }
        try
        {
            // Один срез времени на запись: иначе на полуночи имя файла и метка внутри разъезжались.
            var now = DateTime.Now;
            var directory = Path.Combine(SelfBuildPaths.WorkspaceRoot, "logs");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"traffic-{now:yyyyMMdd}.log");
            lock (Lock)
            {
                var info = new FileInfo(file);
                if (info.Exists && info.Length >= MaxDailyBytes)
                {
                    NoteCap(now, file);
                    return;
                }
                var entry = $"\n===== {now:O} =====\n--- PROMPT ({prompt.Length} chars) ---\n{prompt}\n--- OUTPUT ({output.Length} chars) ---\n{output}\n";
                File.AppendAllText(file, entry);
            }
        }
        catch (Exception exception)
        {
            NoteError(DateTime.Now, exception);
        }
    }

    private static void NoteError(DateTime now, Exception exception)
    {
        if (now - _lastErrorNote < TimeSpan.FromHours(1))
        {
            return;
        }
        _lastErrorNote = now;
        AppEventLog.Log($"TrafficLog: ошибка записи ({exception.GetType().Name}: {exception.Message}); повторные — не чаще раза в час.");
    }

    private static void NoteCap(DateTime now, string file)
    {
        if (now.Date != _lastCapNote.Date)
        {
            _lastCapNote = now;
            AppEventLog.Log($"TrafficLog: дневной файл {Path.GetFileName(file)} достиг потолка {MaxDailyBytes / 1024 / 1024} МБ — запись на сегодня остановлена.");
        }
    }
}
