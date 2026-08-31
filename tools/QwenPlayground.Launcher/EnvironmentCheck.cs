using System.Diagnostics;
using System.IO;
using System.Net.Http;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Launcher;

/// <summary>
/// Проверка окружения: установлены ли необходимые инструменты для сборки и запуска.
/// </summary>
public static class EnvironmentCheck
{
    public sealed record CheckResult(string Name, bool Installed, string? Version, string? Hint);

    /// <summary>
    /// Проверить все необходимые инструменты. Возвращает список результатов.
    /// </summary>
    public static List<CheckResult> CheckAll()
    {
        var results = new List<CheckResult>
        {
            CheckCommand("dotnet", "--version", "Установи .NET 10 SDK: https://dotnet.microsoft.com/download"),
            CheckCommand("git", "--version", "Установи Git: https://git-scm.com/download/win"),
        };
        return results;
    }

    /// <summary>
    /// Можно ли пересобрать проект (dotnet + git установлены).
    /// </summary>
    public static bool CanRebuild()
    {
        return CheckCommand("dotnet", "--version", null).Installed
            && CheckCommand("git", "--version", null).Installed;
    }

    /// <summary>
    /// Проверить, доступен ли llama.cpp сервер (по эндпоинту из settings.json).
    /// </summary>
    public static async Task<CheckResult> CheckLlamaServerAsync()
    {
        try
        {
            var settingsPath = Path.Combine(SelfBuildPaths.WorkspaceRoot, "settings.json");
            if (!File.Exists(settingsPath))
            {
                return new CheckResult("llama.cpp", false, null, "Нет settings.json");
            }
            var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
            var endpoint = json.RootElement.TryGetProperty("Endpoint", out var ep)
                ? ep.GetString() ?? "http://localhost:8080"
                : "http://localhost:8080";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await http.GetAsync(endpoint + "/health");
            if (response.IsSuccessStatusCode)
            {
                return new CheckResult("llama.cpp", true, endpoint, null);
            }
            return new CheckResult("llama.cpp", false, endpoint, "Сервер не отвечает");
        }
        catch
        {
            return new CheckResult("llama.cpp", false, null, "Недоступен");
        }
    }

    private static CheckResult CheckCommand(string name, string versionArg, string? hint)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = name,
                Arguments = versionArg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var output = process.StandardOutput.ReadLine()?.Trim() ?? "";
            process.WaitForExit(5000);
            return new CheckResult(name, true, output, null);
        }
        catch
        {
            return new CheckResult(name, false, null, hint);
        }
    }
}
