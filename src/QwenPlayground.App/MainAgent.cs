using System.IO;

namespace QwenPlayground.App;

/// <summary>
/// «Основной» резидентный агент: постоянная сессия, которая переживает рестарты
/// приложения и в которой живут heartbeat-пробуждения, wake-сигналы и рабочая
/// переписка. Это — долгосрочная память агента.
///
/// Начиная с перехода на слоистую память (2026-08-18) сессия main живёт в отдельной
/// папке sessions/main/: chat.json (история) + layers.json (слои L1/L2/L3) + место
/// под сопутствующие файлы (медиа и т.п.). Идентичность (main-agent.md) и слои
/// инжектятся в системный промпт при сборке, а не пишутся в историю.
/// </summary>
public static class MainAgent
{
    public const string SessionId = "main";
    /// <summary>Имя файла истории main-сессии внутри папки сессии.</summary>
    public const string ChatFileId = "chat";
    public const string IdentityFileName = "main-agent.md";

    private const string DefaultIdentity = """
        Ты — основной резидентный агент QwenPlayground. Эта сессия — твоя постоянная память: в ней живут heartbeat-пробуждения и рабочая переписка; приложение перезапускается, история сохраняется.
        Записная книжка проекта — refactoring.md: найденные проблемы, changelog, backlog. Обновляй её после значимых изменений.
        На heartbeat: проверяй незакрытые пункты записной книжки, делай только малые и безопасные шаги; крупные рефакторинги — по явному запросу. Если изменил код приложения — заверши rebuild_self. Если делать нечего — ответь одной строкой и остановись.
        Правила: не коммитить без явной просьбы; не удалять данные пользователя без подтверждения; run/ трогать только через rebuild_self; класс = файл; комментировать несамоочевидное.
        """;

    /// <summary>Идентичность из main-agent.md; при отсутствии файла — встроенный дефолт.</summary>
    public static string LoadIdentity(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, IdentityFileName);
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path).Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }
        return DefaultIdentity;
    }
}
