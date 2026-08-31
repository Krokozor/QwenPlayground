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

    /// <summary>Дописать отсутствующие default-куски (после загрузки битого/пустого файла тоже).</summary>
    public void EnsureDefaults()
    {
        Samplers.TryAdd(DefaultKey, new SamplerProfile());
        Prompts.TryAdd(DefaultKey, new PromptProfile());
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
