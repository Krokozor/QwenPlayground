using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Memory;

/// <summary>
/// Записная книжка проекта: файл refactoring.md в корне воркспейса (проблемы, changelog,
/// backlog). На неё ссылаются системный промпт (MainAgent.DefaultIdentity), heartbeat-промпт
/// (HeartbeatController.DefaultPrompt) и комментарии в коде — на чужом клоне файла нет, и
/// каждая ссылка становилась фантомом, который агент тратит ход на «разгадывание».
/// Файл принадлежит юзеру и не пушится в git (.gitignore): при отсутствии приложение
/// создаёт его из нейтрального шаблона (паттерн TrajectoryStore / MainAgent.DefaultIdentity) —
/// тогда все ссылки в промптах гарантированно ведут на существующий файл.
/// </summary>
public sealed class ProjectNotebookStore
{
    public const string FileName = "refactoring.md";

    private readonly string _filePath;

    public ProjectNotebookStore(string? directory = null)
    {
        _filePath = Path.Combine(directory ?? SelfBuildPaths.WorkspaceRoot, FileName);
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Нейтральный шаблон для свежего клона/форка: не персонализирован и не пушится в git —
    /// владелец (или агент по его просьбе) заполняет своими проблемами и backlog'ом.
    /// </summary>
    private const string DefaultContent = """
        # Refactoring.md — записная книжка проекта

        Рабочие заметки по улучшению проекта: найденные проблемы, changelog, backlog.
        Файл принадлежит владельцу клона; агент читает его на heartbeat (незакрытые пункты)
        и дописывает сюда по мере работы. Заполните своими принципами и задачами —
        пока здесь только структура.

        ## Принципы

        - (Добавь свои: приоритеты, инварианты, конвенции проекта.)

        ## Backlog

        - (Незакрытые пункты: что сделать, почему, как проверить.)

        ## Changelog

        - (Краткие записи по датам: что найдено/сделано/почему.)
        """;

    /// <summary>
    /// Файл правит владелец и агент параллельно — исчезновение между проверкой и чтением
    /// не роняет сборку промпта. Если файла нет (свежий клон) — создаём из нейтрального
    /// шаблона: ссылки в системном промпте и heartbeat'е должны вести на существующий файл.
    /// </summary>
    public string Load()
    {
        try
        {
            var text = File.ReadAllText(_filePath).Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }
        catch (FileNotFoundException)
        {
            // файл нет — ниже создаём.
        }
        catch (DirectoryNotFoundException)
        {
            return string.Empty;
        }
        // Файла нет (или он пуст) — создаём из нейтрального шаблона. Пустой файл НЕ
        // затираем: он может быть чужим артефактом (нулевой байт из клона), и затирать
        // чужое — хуже, чем отдать шаблон в промпт без правки диска.
        try
        {
            if (!File.Exists(_filePath))
            {
                File.WriteAllText(_filePath, DefaultContent);
            }
        }
        catch
        {
            // Недоступный для записи workspace не должен ронять сборку промпта.
        }
        return DefaultContent.Trim();
    }
}
