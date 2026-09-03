using System.Text.Json;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.Tools;

/// <summary>
/// Активные полки (группы инструментов) сессии: файл shelves.json в каталоге сессии.
/// Состояние инструментов — часть состояния сессии: каждая сессия имеет свой набор активных
/// полок (main — sessions/main, остальные — sessions/&lt;id&gt;). Активация/деактивация меняет
/// системный промпт (инвалидирует KV-кеш) — см. <see cref="ToolGroup"/>.
/// </summary>
public sealed class ShelfState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;

    public ShelfState(string? directory = null)
    {
        _directory = directory ?? Path.Combine(SelfBuildPaths.WorkspaceRoot, "sessions", "main");
        System.IO.Directory.CreateDirectory(_directory);
    }

    public string FilePath => Path.Combine(_directory, "shelves.json");

    /// <summary>Файл отложенных деактиваций: группы, помеченные к снятию при ближайшей смене промпта.</summary>
    public string PendingFilePath => Path.Combine(_directory, "shelves_pending.json");

    public HashSet<ToolGroup> Load() => LoadFile(FilePath);

    /// <summary>Отложенные деактивации (стэйджед): снятся при ближайшей естественной смене промпта.</summary>
    public HashSet<ToolGroup> LoadPending() => LoadFile(PendingFilePath);

    public void Save(IEnumerable<ToolGroup> groups) => SaveFile(FilePath, groups);

    public void SavePending(IEnumerable<ToolGroup> groups) => SaveFile(PendingFilePath, groups);

    /// <summary>
    /// Пометить группу к отложенной деактивации (staged): не трогает active — группа остаётся
    /// в промпте, пока не произойдёт естественная смена системного промпта (см. FlushPending).
    /// Идемпотентно: повторная пометка той же группы не меняет состояние.
    /// </summary>
    public void MarkPending(ToolGroup group)
    {
        var pending = LoadPending();
        if (pending.Add(group))
        {
            SavePending(pending);
        }
    }

    /// <summary>Снять пометку отложенной деактивации (пере-активация отменяет решение на выключение).</summary>
    public void UnmarkPending(ToolGroup group)
    {
        var pending = LoadPending();
        if (pending.Remove(group))
        {
            SavePending(pending);
        }
    }

    /// <summary>
    /// Применить отложенные деактивации: снять помеченные группы из active, очистить pending.
    /// Вызывается ТОЛЬКО когда системный промпт и так меняется (сравнение с кэшем) — чтобы
    /// деактивация не создавала собственный rebuild, а батчилась с неизбежным.
    /// Возвращает снятые группы (пусто, если pending был пуст).
    /// </summary>
    public IEnumerable<ToolGroup> FlushPending()
    {
        var pending = LoadPending();
        if (pending.Count == 0)
        {
            return Enumerable.Empty<ToolGroup>();
        }
        var active = Load();
        var removed = new List<ToolGroup>();
        foreach (var g in pending)
        {
            if (active.Remove(g))
            {
                removed.Add(g);
            }
        }
        if (removed.Count > 0)
        {
            Save(active);
        }
        SavePending(Enumerable.Empty<ToolGroup>());
        return removed;
    }

    /// <summary>
    /// Активные группы, не используемые в разговоре: ни один ToolCall ассистента не ссылается
    /// на инструменты группы. Вызывается после компакции — сжатый сегмент (с его tool_call'ами)
    /// уже не в контексте, и если в остатке группа не нужна, её снимают (бесплатно: компакция
    /// и так пересобирает промпт). Core-группа не рассматривается.
    /// </summary>
    public static IEnumerable<ToolGroup> FindUnused(IReadOnlyCollection<ToolGroup> active,
        IReadOnlyList<ChatMessage> conversation, ToolRegistry registry)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in conversation)
        {
            if (m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 } calls)
            {
                foreach (var call in calls)
                {
                    used.Add(call.Name);
                }
            }
        }
        return active.Where(g => g != ToolGroup.Core &&
                                 !registry.DefinitionsByGroup(g).Any(d => used.Contains(d.Name)));
    }

    private static HashSet<ToolGroup> LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            return new HashSet<ToolGroup>();
        }
        try
        {
            var names = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>();
            return names
                .Where(n => Enum.TryParse<ToolGroup>(n, out _) && Enum.Parse<ToolGroup>(n, ignoreCase: true) != ToolGroup.Core)
                .Select(n => Enum.Parse<ToolGroup>(n, ignoreCase: true))
                .ToHashSet();
        }
        catch (JsonException)
        {
            return new HashSet<ToolGroup>();
        }
    }

    private static void SaveFile(string path, IEnumerable<ToolGroup> groups)
    {
        var names = groups.Where(g => g != ToolGroup.Core).Select(g => g.ToString()).Distinct().OrderBy(n => n).ToList();
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(names, JsonOptions));
    }
}
