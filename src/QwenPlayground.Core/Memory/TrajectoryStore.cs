using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Memory;

/// <summary>
/// Траектория («северная звезда»): файл trajectory.md в корне воркспейса, которым владеет
/// main-агент (текущая цель, подцели, что сделано, что дальше). Инжектится в системный
/// промпт при каждой сборке вместе со слоями памяти — направление переживает рестарты
/// и компакции. Файл правит сам агент (edit_file); здесь — только чтение для инъекции.
/// </summary>
public sealed class TrajectoryStore
{
    private readonly string _filePath;

    public TrajectoryStore(string? directory = null)
    {
        _filePath = Path.Combine(directory ?? SelfBuildPaths.WorkspaceRoot, "trajectory.md");
    }

    public string FilePath => _filePath;

    /// <summary>Файл правит сам агент параллельно — исчезновение между проверкой и чтением не роняет сборку системного промпта.</summary>
    public string Load()
    {
        try
        {
            return File.ReadAllText(_filePath).Trim();
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
        catch (DirectoryNotFoundException)
        {
            return string.Empty;
        }
    }
}
