namespace QwenPlayground.Core.Runtime;

/// <summary>
/// Кусок профиля чата «что уходит в llama.cpp»: семплер + лимиты цикла.
/// Сознательно отделён от настроек системного промпта (<see cref="PromptProfile"/>) и
/// state-блока (<see cref="StateBlockProfile"/>) — это разные зоны ответственности,
/// которые настраиваются и переиспользуются независимо.
///
/// Все поля — строки дома (UI биндит TextBox'ы): пустая строка = унаследовать значение
/// из общих настроек; мусор игнорируется парсером при сборке хода. Профиль default
/// пуст по всем полям — он и есть «глобальные настройки».
/// </summary>
public sealed class SamplerProfile
{
    public string MaxTokens { get; set; } = string.Empty;
    public string Temperature { get; set; } = string.Empty;
    public string TopP { get; set; } = string.Empty;
    public string TopK { get; set; } = string.Empty;
    public string MinP { get; set; } = string.Empty;
    public string RepeatPenalty { get; set; } = string.Empty;
    public string Seed { get; set; } = string.Empty;

    /// <summary>Жёсткий потолок итераций агентного цикла за ход.</summary>
    public string MaxIterations { get; set; } = string.Empty;

    /// <summary>Шагов без sanity_check до nag'а.</summary>
    public string SanityCheckInterval { get; set; } = string.Empty;
}
