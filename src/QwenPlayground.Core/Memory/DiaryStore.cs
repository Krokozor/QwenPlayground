using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Memory;

/// <summary>
/// Эпизодический дневник main-агента: diary.md в корне воркспейса. На каждую компакцию
/// дописывается датированный эпизод (что делал, почему, результат, что дальше) — источник —
/// свежий слой L3 (не отдельный LLM-вызов). Человекочитаемая траектория для
/// владельца-валидатора и для перечитывания самим агентом.
/// </summary>
public sealed class DiaryStore
{
    private static int MaxEntryLength => AppSettings.Get().MemoryDiaryMaxEntryLength;

    private readonly string _filePath;

    public DiaryStore(string? directory = null)
    {
        _filePath = Path.Combine(directory ?? SelfBuildPaths.WorkspaceRoot, "diary.md");
    }

    public string FilePath => _filePath;

    /// <summary>Дописывает датированный эпизод в конец дневника.</summary>
    public void Append(string episode)
    {
        if (string.IsNullOrWhiteSpace(episode))
        {
            return;
        }
        var text = episode.Trim();
        if (text.Length > MaxEntryLength)
        {
            text = text[..MaxEntryLength] + "\n... (обрезано)";
        }
        var entry = "## " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n\n" + text + "\n\n";
        File.AppendAllText(_filePath, entry);
    }
}
