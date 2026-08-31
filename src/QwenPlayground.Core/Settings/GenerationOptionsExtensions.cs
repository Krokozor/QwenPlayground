using System.Globalization;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Runtime;

namespace QwenPlayground.Core.Settings;

/// <summary>
/// Сборка опций генерации из строковых настроек. Числа в settings.json — строки, потому что
/// UI биндит их в TextBox'ы без конвертеров; парсинг инвариантный, при мусоре — дефолт
/// (пользователь не должен ловить падение хода из-за опечатки «0,7» в поле температуры).
/// Кусок-семплер профиля чата переопределяет те же поля точечно.
/// </summary>
public static class GenerationOptionsExtensions
{
    public static GenerationOptions ToGenerationOptions(this AppSettings settings, int? maxTokensOverride = null) => new()
    {
        MaxTokens = maxTokensOverride ?? settings.MaxTokens,
        Temperature = ParseDouble(settings.Temperature, 0.7),
        TopP = ParseDouble(settings.TopP, 0.8),
        TopK = ParseInt(settings.TopK, 20),
        MinP = ParseDouble(settings.MinP, 0),
        RepeatPenalty = ParseDouble(settings.RepeatPenalty, 1.05),
        Seed = int.TryParse(settings.Seed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed)
            ? seed
            : null
    };

    /// <summary>
    /// Опции хода с учётом куска-семплера профиля чата: непустые поля сильнее общих
    /// настроек, пустые/мусорные — унаследовать глобальное. null — ровно глобальный профиль.
    /// </summary>
    public static GenerationOptions ToGenerationOptions(this AppSettings settings, SamplerProfile? sampler) =>
        sampler is null ? settings.ToGenerationOptions() : new GenerationOptions
        {
            MaxTokens = PickInt(sampler.MaxTokens, settings.MaxTokens),
            Temperature = Pick(sampler.Temperature, ParseDouble(settings.Temperature, 0.7)),
            TopP = Pick(sampler.TopP, ParseDouble(settings.TopP, 0.8)),
            TopK = PickInt(sampler.TopK, ParseInt(settings.TopK, 20)),
            MinP = Pick(sampler.MinP, ParseDouble(settings.MinP, 0)),
            RepeatPenalty = Pick(sampler.RepeatPenalty, ParseDouble(settings.RepeatPenalty, 1.05)),
            Seed = int.TryParse(sampler.Seed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed)
                ? seed
                : NullIfUnset(settings.Seed)
        };

    /// <summary>Лимит итераций цикла из куска-семплера; пусто/мусор — из настроек.</summary>
    public static int ResolveMaxIterations(this AppSettings settings, SamplerProfile? sampler) =>
        sampler is not null && int.TryParse(sampler.MaxIterations, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : settings.MaxIterations;

    /// <summary>Интервал самопроверки из куска-семплера; пусто/мусор — из настроек.</summary>
    public static int ResolveSanityCheckInterval(this AppSettings settings, SamplerProfile? sampler) =>
        sampler is not null && int.TryParse(sampler.SanityCheckInterval, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : settings.SanityCheckInterval;

    private static double Pick(string goalValue, double inherited) =>
        double.TryParse(goalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : inherited;

    private static int PickInt(string goalValue, int inherited) =>
        int.TryParse(goalValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : inherited;

    private static int? NullIfUnset(string settingsSeed) =>
        int.TryParse(settingsSeed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) ? seed : null;

    private static double ParseDouble(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
