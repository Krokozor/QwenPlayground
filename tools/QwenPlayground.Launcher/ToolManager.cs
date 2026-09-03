using System.IO;
using System.IO.Compression;
using System.Net.Http;
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
}
