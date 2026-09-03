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

    /// <summary>
    /// Нейтральный шаблон для свежего клона/форка: файл не персонализирован и не пушится
    /// в git (.gitignore) — каждый владелец формулирует свою траекторию сам.
    /// </summary>
    private const string DefaultContent = """
        # Траектория

        Ты — main-агент QwenPlayground. Этот файл — твоя «северная звезда»: текущая цель,
        подцели, что уже сделано, что делать дальше. Ты владеешь этим файлом: правь его
        (edit_file) по мере продвижения — направление должно переживать рестарты и компакции.

        ## Текущая цель

        (пусто — сформулируй, когда появится задача)

        ## Сделано

        - Приложение запущено из рабочего клона.

        ## Дальше

        (по мере работы)
        """;

    /// <summary>
    /// Файл правит сам агент параллельно — исчезновение между проверкой и чтением не роняет
    /// сборку системного промпта. Если файла нет (свежий клон) — создаём из нейтрального
    /// шаблона, чтобы у main-агента сразу была «северная звезда» под своё редактирование.
    /// </summary>
    public string Load()
    {
        try
        {
            return File.ReadAllText(_filePath).Trim();
        }
        catch (FileNotFoundException)
        {
            try
            {
                File.WriteAllText(_filePath, DefaultContent);
                return DefaultContent.Trim();
            }
            catch
            {
                return string.Empty;
            }
        }
        catch (DirectoryNotFoundException)
        {
            return string.Empty;
        }
    }
}
