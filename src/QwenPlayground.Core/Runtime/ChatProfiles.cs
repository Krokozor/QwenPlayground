using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Runtime;

/// <summary>
/// Статичное хранилище профилей чата (config/chat-profiles.json) — тот же паттерн
/// pull-модели, что AppSettings: читается в точке использования, доступно из любого
/// места без прокидывания параметров. Решение владельца (2026-08-25): профили остаются
/// статичными и живут в настройках, а не в экземплярах-каталогах.
///
/// Три независимых словаря — по одному на кусок конфигурации (семплер / промпт /
/// state-блок); сессия ссылается на каждый кусок СВОИМ ключом (SessionData.SamplerKey /
/// PromptKey / StateBlockKey), null = кусок default. Запись default гарантирована:
/// отсутствующий или битый файл создаётся с дефолтами при первом Get(), удалённый
/// кусок всегда восстанавливается — «просрать профиль» невозможно.
///
/// Профиль default пуст по переопределяемым полям и означает «вести себя ровно как
/// раньше»: глобальные настройки + полное поведение цикла.
/// </summary>
[SettingsFile("config/chat-profiles.json")]
public sealed class ChatProfileSet
{
    /// <summary>Ключ обязательного дефолтного профиля каждого куска.</summary>
    public const string DefaultKey = "default";

    public const string FileName = "config/chat-profiles.json";

    public Dictionary<string, SamplerProfile> Samplers { get; set; } = new();
    public Dictionary<string, PromptProfile> Prompts { get; set; } = new();
    public Dictionary<string, StateBlockProfile> StateBlocks { get; set; } = new();

    /// <summary>Глобальный доступ (паттерн AppSettings). Гарантирует наличие default-кусков.</summary>
    public static ChatProfileSet Get()
    {
        var set = SettingsStore<ChatProfileSet>.Get();
        set.EnsureDefaults();
        return set;
    }

    /// <summary>Записать живой экземпляр атомарно.</summary>
    public void Save() => SettingsStore<ChatProfileSet>.Save();

    /// <summary>Ключ профиля субагентов (spawn_subagent): код-управляемый, но правится в редакторе профилей.</summary>
    public const string SubagentPromptKey = "subagent";

    /// <summary>Дописать отсутствующие default-куски (после загрузки битого/пустого файла тоже).</summary>
    public void EnsureDefaults()
    {
        Samplers.TryAdd(DefaultKey, new SamplerProfile());
        Prompts.TryAdd(DefaultKey, new PromptProfile());
        // Профиль субагента: идентичность + правила + контракт отчёта + «всё, кроме спавна/самосборки»
        // (без рекурсии). Паттерн промпта — по opencode (role → strengths → guidelines → output rules);
        // докручивается в редакторе профилей (ключ «subagent»).
        Prompts.TryAdd(SubagentPromptKey, new PromptProfile {
            SystemPrompt =
                "Ты — субагент: специализированный рабочий, который АВТОНОМНО выполняет задачу, заказанную main-агентом.\n\n" +
                "Контекст:\n" +
                "- Контекст main-агента ты НЕ видишь — вся информация о задаче в сообщении-заказе.\n" +
                "- Спрашивать не у кого: ты работаешь до финального сообщения. Чего-то не хватает — сделай " +
                "разумное предположение и пометь его в отчёте.\n\n" +
                "Инструменты (какой для чего):\n" +
                "- read_file / glob / grep — чтение и поиск по файлам проекта;\n" +
                "- write_file / edit_file — создание и правка файлов;\n" +
                "- shell — команды (сборка, тесты, скрипты);\n" +
                "- webfetch — веб-страницы и API;\n" +
                "- полки browser / csharp / desktop / mcp — активируй activate_shelf, если задача требует " +
                "(браузер, анализ C#, работа с экраном, внешние приложения);\n" +
                "- memory_* — твоя собственная память (факты о задаче, которые стоит запомнить);\n" +
                "- sanity_check — самопроверка в длинной работе.\n" +
                "Полный список и схемы — в описаниях инструментов и индексе полок.\n\n" +
                "Правила работы:\n" +
                "1. Сначала краткий план (в первых мыслях), затем исполнение.\n" +
                "2. Делай только то, что входит в задачу; не расширяй её по своей инициативе.\n" +
                "3. Если в задаче указана верификация (тесты, сборка, команды) — выполни её и включи результат в отчёт.\n" +
                "4. Не дублируй работу: результат уже сделанного шага есть в твоём контексте — используй его.",
            ResultContract =
                "Финальное сообщение — это ОТЧЁТ для main-агента: самодостаточный, конкретный, без воды. Структура:\n" +
                "- что сделано;\n" +
                "- ключевые результаты (факты, цифры, выводы);\n" +
                "- пути к созданным/изменённым файлам (если есть);\n" +
                "- предположения, которые пришлось сделать (если есть);\n" +
                "- что не получилось и почему (если есть).\n" +
                "Main-агент увидит только этот текст, а не процесс работы. " +
                "Если задача не выполнена — честно скажи, что не получилось и почему.",
            Tools = true,
            DeniedTools = ["spawn_subagent", "rebuild_self"]
        });
        StateBlocks.TryAdd(DefaultKey, new StateBlockProfile());
    }

    /// <summary>Семплер по ключу сессии; null/неизвестный ключ → default.</summary>
    public SamplerProfile ResolveSampler(string? key) =>
        !string.IsNullOrEmpty(key) && Samplers.TryGetValue(key, out var sampler) ? sampler : Samplers[DefaultKey];

    /// <summary>Профиль промпта по ключу сессии; null/неизвестный ключ → default.</summary>
    public PromptProfile ResolvePrompt(string? key) =>
        !string.IsNullOrEmpty(key) && Prompts.TryGetValue(key, out var prompt) ? prompt : Prompts[DefaultKey];

    /// <summary>Профиль state-блока по ключу сессии; null/неизвестный ключ → default.</summary>
    public StateBlockProfile ResolveStateBlock(string? key) =>
        !string.IsNullOrEmpty(key) && StateBlocks.TryGetValue(key, out var block) ? block : StateBlocks[DefaultKey];
}

/// <summary>Фасад pull-доступа к профилям: <c>ChatProfiles.Get().ResolveSampler(key)</c>.</summary>
public static class ChatProfiles
{
    public static ChatProfileSet Get() => ChatProfileSet.Get();
}
