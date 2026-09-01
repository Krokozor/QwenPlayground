using System.Diagnostics;
using System.Runtime.Versioning;

namespace QwenPlayground.Core.Crash;

/// <summary>
/// Выжимка Windows Event Log по процессу. Нативные смерти (access violation,
/// падение в нативном модуле, StackOverflow вне managed) не оставляют следов
/// в managed-логах — единственный источник «почему» там: журнал Application
/// (источники .NET Runtime, Application Error).
/// </summary>
[SupportedOSPlatform("windows")]
public static class EventLogExcerpt
{
    public static string? ForProcess(string processName, int minutes = 15, int maxEntries = 3)
    {
        try
        {
            var cutoff = DateTime.Now.AddMinutes(-minutes);
            using var log = new EventLog("Application");
            var lines = new List<string>();
            // Entries идут от новых к старым.
            foreach (EventLogEntry entry in log.Entries)
            {
                if (entry.TimeGenerated < cutoff)
                {
                    break;
                }
                if (entry.EntryType is not (EventLogEntryType.Error or EventLogEntryType.Warning))
                {
                    continue;
                }
                if (!entry.Message.Contains(processName, StringComparison.OrdinalIgnoreCase) &&
                    entry.Source != ".NET Runtime")
                {
                    continue;
                }
                lines.Add($"[{entry.TimeGenerated:HH:mm:ss}] {entry.Source}:\n{Truncate(entry.Message, 600)}");
                if (lines.Count >= maxEntries)
                {
                    break;
                }
            }
            return lines.Count == 0 ? null : string.Join("\n\n", lines);
        }
        catch
        {
            return null; // нет доступа к Event Log — файловый лог основной канал
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\n... (обрезано)";
}
