using QwenPlayground.Core.Runtime;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Кусок-семплер профиля чата: непустые поля сильнее общих настроек, пустые и мусорные
/// — унаследовать глобальное; лимиты цикла резолвятся тем же правилом.
/// </summary>
public sealed class SamplerProfileTests
{
    private static AppSettings Settings() => new()
    {
        MaxTokens = 2048,
        Temperature = "0.7",
        TopP = "0.8",
        TopK = "20",
        MinP = "0",
        RepeatPenalty = "1.05",
        Seed = "42",
        MaxIterations = 50,
        SanityCheckInterval = 20
    };

    [Fact]
    public void NullSampler_EqualsBaseProfile()
    {
        var fromNull = Settings().ToGenerationOptions((SamplerProfile?)null);
        var baseLine = Settings().ToGenerationOptions();

        Assert.Equal(baseLine.MaxTokens, fromNull.MaxTokens);
        Assert.Equal(baseLine.Temperature, fromNull.Temperature);
    }

    [Fact]
    public void Overrides_WinOverSettings()
    {
        var sampler = new SamplerProfile
        {
            MaxTokens = "512",
            Temperature = "0.2",
            TopP = "0.5",
            TopK = "10",
            MinP = "0.05",
            RepeatPenalty = "1.2",
            Seed = "7"
        };
        var options = Settings().ToGenerationOptions(sampler);

        Assert.Equal(512, options.MaxTokens);
        Assert.Equal(0.2, options.Temperature);
        Assert.Equal(0.5, options.TopP);
        Assert.Equal(10, options.TopK);
        Assert.Equal(0.05, options.MinP);
        Assert.Equal(1.2, options.RepeatPenalty);
        Assert.Equal(7, options.Seed);
    }

    [Fact]
    public void EmptyAndGarbage_InheritFromSettings()
    {
        var sampler = new SamplerProfile { Temperature = "", TopP = "не число" };
        var options = Settings().ToGenerationOptions(sampler);

        Assert.Equal(2048, options.MaxTokens);
        Assert.Equal(0.7, options.Temperature); // пусто → из настроек
        Assert.Equal(0.8, options.TopP); // мусор → из настроек
        Assert.Equal(1.05, options.RepeatPenalty);
    }

    [Fact]
    public void Seed_EmptyInSampler_FallsBackToSettingsSeed()
    {
        Assert.Equal(42, Settings().ToGenerationOptions(new SamplerProfile()).Seed);

        var unset = new AppSettings { Seed = "" }.ToGenerationOptions(new SamplerProfile());
        Assert.Null(unset.Seed);
    }

    [Fact]
    public void LoopLimits_ResolveFromSampler_OrInherit()
    {
        var settings = Settings();

        // Пустой семплер — глобальные лимиты.
        Assert.Equal(50, settings.ResolveMaxIterations(new SamplerProfile()));
        Assert.Equal(20, settings.ResolveSanityCheckInterval(new SamplerProfile()));

        // Переопределение сильнее, мусор игнорируется.
        var tuned = new SamplerProfile { MaxIterations = "120", SanityCheckInterval = "мусор" };
        Assert.Equal(120, settings.ResolveMaxIterations(tuned));
        Assert.Equal(20, settings.ResolveSanityCheckInterval(tuned));
    }
}
