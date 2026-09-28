using QwenPlayground.Launcher;

namespace QwenPlayground.Launcher.Tests;

/// <summary>
/// Live-проверка «Проверить обновления» против настоящего GitHub API.
/// Гейт зелёный без сети (как остальные live-тесты репозитория).
///
/// Регресс-guard на главный баг: api.github.com отвечает 403 «make sure your request has a
/// User-Agent header», а HttpClient заголовок не добавляет сам. Без него ВСЯ проверка
/// обновлений молча возвращала «проверка не удалась», и сайдкар не писался вовсе.
/// </summary>
public sealed class ToolManagerLiveTests
{
    private static bool NetworkAvailable()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            // User-Agent обязателен и здесь: без него api.github.com отвечает 403 и гейт
            // тихо возвращает false — тест был бы зелёным, не проверив ничего.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("QwenPlayground-Launcher-Tests");
            using var response = http.GetAsync("https://api.github.com/rate_limit").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static TheoryData<string> ToolNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in LauncherConfig.CreateDefault().Tools.Keys)
        {
            data.Add(name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void LatestRelease_Live_ResolvesAssetWithDigest(string toolName)
    {
        if (!NetworkAvailable())
        {
            return;
        }
        var tool = LauncherConfig.CreateDefault().Tools[toolName];

        var (release, error) = ToolManager.TryGetLatestReleaseAsync(tool).GetAwaiter().GetResult();

        Assert.True(release is not null, $"GitHub API не ответил: {error}");
        Assert.NotEmpty(release!.Tag);
        Assert.NotEmpty(release.PublishedAt);
        Assert.NotEmpty(release.AssetName);
        // Пустой digest = в сайдкар писать нечего, и «Проверить обновления» ослепнет.
        Assert.StartsWith("sha256:", release.Digest);
        Assert.Contains(release.AssetName, release.DownloadUrl);
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void VersionProbe_Live_ReadsVersionFromInstalledTool(string toolName)
    {
        if (!NetworkAvailable())
        {
            return;
        }
        var tool = LauncherConfig.CreateDefault().Tools[toolName];
        if (!tool.IsInstalled())
        {
            return; // инструмент не ставили — зонд нечего проверять
        }

        var version = ToolManager.GetInstalledVersionAsync(tool).GetAwaiter().GetResult();

        // Раньше зонд читал только stdout: poppler печатает версию в stderr и «-version»
        // не понимает, из-за чего ячейка версии была пустой без всякой ошибки.
        Assert.False(string.IsNullOrWhiteSpace(version), "зонд версии вернул пустоту");
    }
}
