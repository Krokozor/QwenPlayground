using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Launcher;

/// <summary>
/// Управление инструментами (ffmpeg и др.): скачивание, экстракция, проверка версий.
/// </summary>
public static class ToolManager
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private static string Log(string message)
    {
        var logPath = Path.Combine(SelfBuildPaths.RunRoot, "launcher.log");
        var line = $"[{DateTime.Now:O}] [tool] {message}";
        File.AppendAllText(logPath, line + "\n");
        return line;
    }

    /// <summary>
    /// Проверить, установлен ли инструмент.
    /// </summary>
    public static bool IsInstalled(ToolConfig tool)
    {
        return tool.IsInstalled();
    }

    /// <summary>
    /// Получить версию установленного инструмента (выполняет bin --version).
    /// </summary>
    public static async Task<string?> GetInstalledVersionAsync(ToolConfig tool)
    {
        var binPath = Path.Combine(SelfBuildPaths.WorkspaceRoot, tool.BinPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(binPath)) return null;

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = binPath,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = new System.Diagnostics.Process { StartInfo = startInfo };
            process.Start();
            var output = await process.StandardOutput.ReadLineAsync() ?? "";
            await process.WaitForExitAsync();
            return output.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Скачать и установить инструмент. Возвращает (success, message).
    /// </summary>
    public static async Task<(bool Success, string Message)> InstallAsync(ToolConfig tool, IProgress<string>? progress = null)
    {
        var extractDir = Path.Combine(SelfBuildPaths.WorkspaceRoot, tool.ExtractTo.Replace('/', Path.DirectorySeparatorChar));
        var safeName = tool.ExtractTo.Replace('/', '_').Replace('\\', '_');
        var tempZip = Path.Combine(Path.GetTempPath(), $"qpw_{safeName}_{DateTime.Now:HHmmss}.zip");

        try
        {
            progress?.Report("Скачивание...");
            Log($"downloading {tool.DownloadUrl}");
            
            using var response = await Http.GetAsync(tool.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            var downloadedBytes = 0L;
            var buffer = new byte[81920];
            int read;

            using (var contentStream = await response.Content.ReadAsStreamAsync())
            using (var fileStream = File.Create(tempZip))
            {
                while ((read = await contentStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    downloadedBytes += read;
                    if (totalBytes > 0)
                    {
                        var percent = (int)(downloadedBytes * 100 / totalBytes);
                        progress?.Report($"Скачивание... {percent}% ({downloadedBytes / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB)");
                    }
                }
            }

            progress?.Report("Экстракция...");
            Log($"extracting to {extractDir}");

            // Удалить старый каталог
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }
            Directory.CreateDirectory(extractDir);

            ZipFile.ExtractToDirectory(tempZip, extractDir, overwriteFiles: true);

            // Проверить, что бинарник на месте
            var binPath = Path.Combine(SelfBuildPaths.WorkspaceRoot, tool.BinPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(binPath))
            {
                return (false, $"Бинарник не найден после экстракции: {binPath}");
            }

            // Best-effort: метаданные сборки для будущей «Проверить обновления».
            var info = await GetLatestAssetInfoAsync(tool);
            if (info is not null)
            {
                SaveAssetInfo(tool, info);
            }

            Log($"installed {tool.ExtractTo} successfully");
            return (true, "Установлено успешно");
        }
        catch (Exception ex)
        {
            Log($"install failed: {ex.Message}");
            return (false, ex.Message);
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
    }

    /// <summary>
    /// Удалить инструмент (каталог).
    /// </summary>
    public static bool Uninstall(ToolConfig tool)
    {
        var extractDir = Path.Combine(SelfBuildPaths.WorkspaceRoot, tool.ExtractTo.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(extractDir))
        {
            Directory.Delete(extractDir, recursive: true);
            Log($"uninstalled {tool.ExtractTo}");
            return true;
        }
        return false;
    }

    // ── Проверка обновлений ─────────────────────────────────────────────────────────

    /// <summary>
    /// «Паспорт» скачанной сборки: digest ассета (sha256) и дата публикации релиза.
    /// Релиз-тег «latest» у авто-сборок обновляется на месте, поэтому именно digest
    /// ассета — честный идентификатор конкретной сборки.
    /// </summary>
    public sealed record AssetInfo(string Digest, string PublishedAt, string AssetName);

    private static readonly HttpClient CheckHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    /// <summary>Сайдкар-файл с метаданными сборки в каталоге экстракции инструмента.</summary>
    private static string AssetInfoPath(ToolConfig tool) =>
        Path.Combine(SelfBuildPaths.WorkspaceRoot, tool.ExtractTo.Replace('/', Path.DirectorySeparatorChar), ".asset-info");

    /// <summary>
    /// Последний релиз из GitHub API (тег «latest»): digest и дата публикации ассета,
    /// совпадающего с именем файла в DownloadUrl. null — URL не GitHub-релиз или сбой сети.
    /// </summary>
    public static async Task<AssetInfo?> GetLatestAssetInfoAsync(ToolConfig tool)
    {
        try
        {
            var uri = new Uri(tool.DownloadUrl);
            if (!uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // /<owner>/<repo>/releases/download/<tag>/<asset-name>
            if (parts.Length < 5 || parts[2] != "releases")
            {
                return null;
            }
            var assetName = parts[^1];

            using var response = await CheckHttp.GetAsync(
                $"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases/latest");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var publishedAt = root.GetProperty("published_at").GetString() ?? "";
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != assetName)
                {
                    continue;
                }
                var digest = asset.GetProperty("digest").GetString() ?? "";
                return new AssetInfo(digest, publishedAt, assetName);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Сохранить метаданные сборки в сайдкар (best-effort).</summary>
    public static void SaveAssetInfo(ToolConfig tool, AssetInfo info)
    {
        try
        {
            File.WriteAllText(AssetInfoPath(tool), $"{info.Digest}\n{info.PublishedAt}\n");
        }
        catch
        {
            // Метаданные — не критичны для установки.
        }
    }

    /// <summary>Прочитать метаданные локальной сборки. null — не установлены/нет сайдкора.</summary>
    public static AssetInfo? LoadAssetInfo(ToolConfig tool)
    {
        try
        {
            var lines = File.ReadAllLines(AssetInfoPath(tool));
            if (lines.Length >= 2)
            {
                return new AssetInfo(lines[0], lines[1], string.Empty);
            }
        }
        catch
        {
            // Нет сайдкора.
        }
        return null;
    }

    /// <summary>
    /// Настоящая проверка обновлений: digest локальной сборки (сайдкар) против
    /// последнего релиза из GitHub API. Возвращает человекочитаемое сообщение.
    /// </summary>
    public static async Task<string> CheckUpdateAsync(ToolConfig tool)
    {
        if (!tool.IsInstalled())
        {
            return "не установлен";
        }
        var latest = await GetLatestAssetInfoAsync(tool);
        if (latest is null)
        {
            return "проверка не удалась: источник не является GitHub-релизом или нет сети";
        }
        var local = LoadAssetInfo(tool);
        if (local is null)
        {
            return $"последняя сборка {latest.PublishedAt}; локально нет метаданных версии (установлен до их введения) — нажмите «Скачать», чтобы обновить";
        }
        return latest.Digest == local.Digest
            ? $"актуальная версия (сборка {latest.PublishedAt})"
            : $"доступна новая сборка: {latest.PublishedAt} (ваша: {local.PublishedAt}) — нажмите «Скачать»";
    }
}
