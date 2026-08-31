using System.Text.Json;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.SelfBuild;

public sealed class BuildJournalEntry
{
    public required string Id { get; set; }
    public DateTime Timestamp { get; set; }
    public int BuildExitCode { get; set; }
    public string BuildOutputTail { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string? FailureReason { get; set; }
    public bool Announced { get; set; }
    // Полные логи (путь к файлу) — для детальной диагностики без потери в tail.
    public string? BuildLogPath { get; set; }
    public string? GateLogPath { get; set; }
    public int? GateExitCode { get; set; }
}

public static class BuildJournal
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<BuildJournalEntry> Load(string runRoot)
    {
        var file = Path.Combine(runRoot, "journal.json");
        if (!File.Exists(file))
        {
            return new List<BuildJournalEntry>();
        }
        try
        {
            return JsonSerializer.Deserialize<List<BuildJournalEntry>>(File.ReadAllText(file), Options)
                   ?? new List<BuildJournalEntry>();
        }
        catch (JsonException)
        {
            return new List<BuildJournalEntry>();
        }
    }

    public static void Append(string runRoot, BuildJournalEntry entry)
    {
        var entries = Load(runRoot);
        entries.Add(entry);
        Save(runRoot, entries);
    }

    public static void UpdateLast(string runRoot, string status, string? failureReason)
    {
        var entries = Load(runRoot);
        if (entries.Count == 0)
        {
            return;
        }
        entries[^1].Status = status;
        entries[^1].FailureReason = failureReason;
        Save(runRoot, entries);
    }

    public static void MarkAnnounced(string runRoot, IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids);
        var entries = Load(runRoot);
        foreach (var entry in entries)
        {
            if (set.Contains(entry.Id))
            {
                entry.Announced = true;
            }
        }
        Save(runRoot, entries);
    }

    private static void Save(string runRoot, List<BuildJournalEntry> entries)
    {
        Directory.CreateDirectory(runRoot);
        AtomicFile.WriteAllText(
            Path.Combine(runRoot, "journal.json"), JsonSerializer.Serialize(entries, Options));
    }
}
