using QwenPlayground.Launcher;

namespace QwenPlayground.Launcher.Tests;

/// <summary>
/// Подстановка тега релиза в шаблон URL. Нужна, потому что имя ассета poppler
/// содержит версию (Release-26.09.0-0.zip): с зашпиненным URL «Проверить обновлений»
/// навсегда видел бы одну и ту же сборку, а с шаблоном — последний релиз.
/// </summary>
public sealed class ToolManagerTagTests
{
    [Theory]
    [InlineData("v26.09.0-0", "26.09.0-0")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("latest", "latest")]
    [InlineData("release-2026.09.0", "release-2026.09.0")]
    [InlineData("vnext", "vnext")]
    public void VersionFromTag_StripsOnlyLeadingVBeforeDigit(string tag, string expected)
    {
        Assert.Equal(expected, ToolManager.VersionFromTag(tag));
    }

    [Fact]
    public void PopplerTemplate_ResolvesToRealAssetName()
    {
        var template =
            "https://github.com/oschwartz10612/poppler-windows/releases/download/{tag}/Release-{version}.zip";

        var url = ToolManager.SubstituteReleaseTags(template, "v26.09.0-0");

        Assert.Equal(
            "https://github.com/oschwartz10612/poppler-windows/releases/download/v26.09.0-0/Release-26.09.0-0.zip",
            url);
    }

    [Fact]
    public void FfmpegTemplate_WithoutPlaceholders_IsUnchanged()
    {
        const string template =
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

        Assert.Equal(template, ToolManager.SubstituteReleaseTags(template, "latest"));
    }
}

/// <summary>
/// Конфигурация инструмента: BinPath должен проверяться относительно переданного корня
/// (поле «Корень проекта» в настройках лаунчера), а дефолты — рабочими без правок.
/// </summary>
public sealed class ToolConfigTests
{
    [Fact]
    public void Defaults_SupportBothCurrentSources()
    {
        var defaults = LauncherConfig.CreateDefault();

        Assert.Equal("-version", defaults.Tools["ffmpeg"].VersionArgs);
        // poppler не понимает -version (печатает I/O Error и код 1), у него -v и stderr.
        Assert.Equal("-v", defaults.Tools["poppler"].VersionArgs);
        Assert.All(defaults.Tools.Values, t => Assert.True(t.StripTopLevelDir));
    }

    [Fact]
    public void DefaultBinPaths_AreVersionIndependent()
    {
        var defaults = LauncherConfig.CreateDefault();

        // Номер версии в BinPath = поломка на следующем релизе.
        Assert.DoesNotContain("ffmpeg-master", defaults.Tools["ffmpeg"].BinPath);
        Assert.DoesNotMatch(@"\d+\.\d+", defaults.Tools["poppler"].BinPath);
    }

    [Fact]
    public void DefaultPopplerUrl_TracksLatestRelease()
    {
        var url = LauncherConfig.CreateDefault().Tools["poppler"].DownloadUrl;

        Assert.Contains("{tag}", url);
        Assert.Contains("{version}", url);
    }
}
