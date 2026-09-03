using System.Text.Json.Serialization;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Settings;

/// <summary>
/// Настройки приложения (chat-профиль + сервисные адреса + расписание агента).
/// Живут в SettingsStore&lt;AppSettings&gt; — читайте в точке использования:
/// <c>AppSettings.Get().Endpoint</c>, а не через параметры сигнатур.
/// Поля-строки для чисел (Temperature/TopP/...) — сознательно: UI биндит TextBox'ы,
/// парсинг с дефолтами при мусоре делает <see cref="GenerationOptionsExtensions"/>.
/// </summary>
[SettingsFile("settings.json")]
public sealed class AppSettings
{
    /// <summary>Глобальный доступ (паттерн NekoBot). Первый вызов читает settings.json.</summary>
    public static AppSettings Get() => SettingsStore<AppSettings>.Get();

    /// <summary>Сохранить текущий экземпляр атомарно.</summary>
    public static void Save() => SettingsStore<AppSettings>.Save();

    /// <summary>
    /// Write-through: атомарно применить мутацию к живому экземпляру и сохранить на диск.
    /// Правка только файла не подействует на работающий процесс — живой экземпляр источник
    /// правды, поэтому мутация и запись идут одним действием.
    /// </summary>
    public static void Update(Action<AppSettings> mutate) => SettingsStore<AppSettings>.Update(mutate);

    /// <summary>
    /// Доступна ли компаньон-модель (агент на соседнем компьютере) для проб — единый источник
    /// правды для опциональности фичи. Нужно И тумблер CompanionEnabled (вкл), И непустой адрес.
    /// Выкл/пусто = пробы (классификация/дедуп/sanity/rerank) не летят, память работает по тексту.
    /// </summary>
    public static bool CompanionConfigured =>
        Get().CompanionEnabled && !string.IsNullOrWhiteSpace(Get().CompanionEndpoint);

    /// <summary>
    /// Мастер-переключатель памяти агента (вручную, независимо от корректности остальных настроек).
    /// Выкл = нет реколла (post-turn/live), всплывших фактов в state-блоке, нага и фоновой
    /// векторизации; тулы memory_* скрыты из промпта (модель не может их вызвать). Ручная вкладка
    /// «Память» остаётся доступной для инспекции фактов на диске. Дефолт true — поведение не меняется.
    /// </summary>
    public bool MemoryEnabled { get; set; } = true;

    public string Endpoint { get; set; } = "http://127.0.0.1:5001";
    public int MaxTokens { get; set; } = 2048;
    public int ContextSize { get; set; } = 32768;
    public string ProjectRoot { get; set; } = Path.Combine(SelfBuildPaths.WorkspaceRoot, "Sandbox");
    /// <summary>Дополнительные рабочие папки (агент может работать и с ними, помимо своего корня).</summary>
    public List<string> AdditionalWorkspaces { get; set; } = new();
    /// <summary>Усилие размышления (эталонные строки из assets/chat_template.jinja): XHigh / Medium / Low.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ReasoningEffort ReasoningEffort { get; set; } = ReasoningEffort.XHigh;
    /// <summary>Автономные пробуждения main-агента: расписание + wake-сигналы из wake/.</summary>
    public bool HeartbeatEnabled { get; set; } = true;
    public int HeartbeatIntervalMinutes { get; set; } = 30;
    /// <summary>Доля недавних сообщений, которую компактация сохраняет дословно (0–1).</summary>
    public string CompactKeepRatio { get; set; } = "0.5";
    /// <summary>
    /// Компаньон-модель для логит-проб (отдельная машина — не трогает наш KV-кеш). ОПЦИОНАЛЬНО:
    /// пусто = фича выключена (пробы не летят, память работает по тексту). Дефолт пусто — для
    /// open-source не зашиваем адрес чужой LAN-машины; владелец задаёт свой в Настройках.
    /// </summary>
    public string CompanionEndpoint { get; set; } = string.Empty;
    /// <summary>
    /// Использовать ли companion-модель для проб (классификация/дедуп/rerank/sanity) — тумблер
    /// рядом с адресом в Настройках. Выкл = пробы НЕ летят (память по тексту, sanity без пробы),
    /// но адрес CompanionEndpoint СОХРАНЯЕТСЯ — вкл обратно в один клик. Отдельно от MemoryEnabled
    /// (мастер памяти) и от пустого адреса: владелец держит модель настроенной, но временно не
    /// даёт агенту её грузить (модель нужна самому). Дефолт true — поведение не меняется.
    /// </summary>
    public bool CompanionEnabled { get; set; } = true;
    public string Temperature { get; set; } = "0.7";
    public string TopP { get; set; } = "0.8";
    public string TopK { get; set; } = "20";
    public string MinP { get; set; } = "0";
    public string RepeatPenalty { get; set; } = "1.05";
    public string Seed { get; set; } = string.Empty;
    /// <summary>Жёсткий потолок итераций агентного цикла. 0 = без лимита (бесконечно).</summary>
    public int MaxIterations { get; set; } = 50;
    /// <summary>Шагов без самопроверки до nag'а sanity_check. 0 = отключено.</summary>
    public int SanityCheckInterval { get; set; } = 20;
    /// <summary>
    /// Пуш на GitHub при самосборке (rebuild_self): git push уже закоммиченных коммитов после
    /// успешного билда. По умолчанию ВЫКЛ — коммиты и пуши делает владелец/агент явно.
    /// </summary>
    public bool PushOnRebuild { get; set; } = false;
    /// <summary>Последняя открытая сессия — восстанавливается при старте (иначе main-агент).</summary>
    public string? LastSessionId { get; set; }

    // ── Память / надмозг ─────────────────────────────────────────────────────────────

    /// <summary>Фактов без слоёв, обогащаемых за один heartbeat-проход классификатора.</summary>
    public int MemoryFlushBudget { get; set; } = 2;
    /// <summary>Проб пар за один проход сканера дубликатов.</summary>
    public int MemoryScanProbeBudget { get; set; } = 4;
    /// <summary>Сколько повторных попаданий нужно, чтобы факт вышел в state-блок (правило стабильности).</summary>
    public int MemorySurfacingThreshold { get; set; } = 2;
    /// <summary>Минимум «токенов» (chars/4) натекшего стрима между live-реколлами.</summary>
    public int MemoryLiveRecallMinTokens { get; set; } = 400;
    /// <summary>Пауза между live-реколлами, секунд.</summary>
    public int MemoryLiveRecallIntervalSec { get; set; } = 10;
    /// <summary>Рендеров без memory_* до nag'а про менеджмент памяти.</summary>
    public int MemoryNagIntervalRenders { get; set; } = 15;
    /// <summary>Сколько фактов возвращает реколл (Top-X до rerank'а).</summary>
    public int RecallTopX { get; set; } = 3;
    /// <summary>Порог релевантности реколла (0..1).</summary>
    public double RecallMinScore { get; set; } = 0.12;
    /// <summary>Балл цифры классификатора, выше которого пара уверенно «похожа».</summary>
    public double SimilaritySimilarMin { get; set; } = 6.0;
    /// <summary>Балл, ниже которого пара уверенно «не похожа».</summary>
    public double SimilarityDistinctMax { get; set; } = 3.0;
    /// <summary>Энтропия распределения цифры выше этой границы → классификатор не уверен.</summary>
    public double SimilarityConfidentMaxEntropy { get; set; } = 1.0;

    // ── Память: параметры проб и скоринга (ранее хардкод в Core) ─────────────────────

    /// <summary>Бюджет токенов вектора диалога (DialogueWindow).</summary>
    public int MemoryDialogueBudgetTokens { get; set; } = 10_000;
    /// <summary>Максимум сообщений в векторе диалога.</summary>
    public int MemoryDialogueMaxMessages { get; set; } = 12;
    /// <summary>Позиций логитов на пробу классификации (nProbs).</summary>
    public int MemoryClassifyNProbs { get; set; } = 52;
    /// <summary>Токенов генерации на пробу классификации (nPredict).</summary>
    public int MemoryClassifyNPredict { get; set; } = 16;
    /// <summary>Позиций логитов на пробу rerank (nProbs).</summary>
    public int MemoryRerankNProbs { get; set; } = 52;
    /// <summary>Токенов генерации на пробу rerank (nPredict).</summary>
    public int MemoryRerankNPredict { get; set; } = 12;
    /// <summary>Максимум кандидатов в SecondPass (rerank).</summary>
    public int MemoryRerankMaxCandidates { get; set; } = 25;
    /// <summary>Длина примера факта в промпте rerank.</summary>
    public int MemoryRerankCandidateContentLength { get; set; } = 200;
    /// <summary>Вес категорий в semantic overlap (0..1, сумма с EmojiWeight = 1).</summary>
    public double MemoryCategoryWeight { get; set; } = 0.7;
    /// <summary>Вес эмодзи в semantic overlap (0..1, сумма с CategoryWeight = 1).</summary>
    public double MemoryEmojiWeight { get; set; } = 0.3;
    /// <summary>Фактов, извлекаемых за одну компакцию.</summary>
    public int MemoryMaxFactsPerCompaction { get; set; } = 5;
    /// <summary>Максимальная длина записи дневника (diary.md).</summary>
    public int MemoryDiaryMaxEntryLength { get; set; } = 3000;
}
