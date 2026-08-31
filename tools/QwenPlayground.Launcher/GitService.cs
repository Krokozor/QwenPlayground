using System.Diagnostics;
using System.IO;
using System.Text;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Launcher;

/// <summary>
/// Git-операции для лаунчера: pull, status, clone.
/// Использует git CLI (уже установлен на системе).
/// </summary>
public static class GitService
{
    private static string Log(string message)
    {
        var logPath = Path.Combine(SelfBuildPaths.RunRoot, "launcher.log");
        var line = $"[{DateTime.Now:O}] [git] {message}";
        File.AppendAllText(logPath, line + "\n");
        return line;
    }

    /// <summary>
    /// Проверить, является ли каталог git-репозиторием.
    /// </summary>
    public static bool IsGitRepo(string path)
    {
        var gitDir = Path.Combine(path, ".git");
        return Directory.Exists(gitDir) || File.Exists(gitDir);
    }

    /// <summary>
    /// Получить текущую HEAD-коммиту (short hash + subject).
    /// </summary>
    public static async Task<string?> GetHeadCommitAsync()
    {
        var (exitCode, output) = await RunGitAsync("log -1 --format=%h %s");
        return exitCode == 0 ? output.Trim() : null;
    }

    /// <summary>
    /// Получить remote URL origin.
    /// </summary>
    public static async Task<string?> GetRemoteUrlAsync()
    {
        var (exitCode, output) = await RunGitAsync("remote get-url origin");
        return exitCode == 0 ? output.Trim() : null;
    }

    /// <summary>
    /// Проверить, есть ли новые коммиты на remote (без pull).
    /// Best-effort: если fetch не удался (нет сети, нет auth) — возвращает false.
    /// </summary>
    public static async Task<bool> HasRemoteUpdatesAsync()
    {
        try
        {
            // fetch без merge (best-effort: может не быть сети или auth)
            var (fetchExit, _) = await RunGitAsync("fetch origin --quiet");
            if (fetchExit != 0) return false;

            // сравнить HEAD с origin/main
            var (exitCode, output) = await RunGitAsync("rev-list HEAD..origin/main --count");
            if (exitCode != 0) return false;

            var count = int.TryParse(output.Trim(), out var n) ? n : 0;
            return count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Git pull из remote. Возвращает (exitCode, output).
    /// </summary>
    public static async Task<(int ExitCode, string Output)> PullAsync()
    {
        Log("pull requested");
        var (exitCode, output) = await RunGitAsync("pull origin main");
        Log($"pull exit {exitCode}");
        return (exitCode, output);
    }

    /// <summary>
    /// Git status (short). Для отображения в UI.
    /// </summary>
    public static async Task<string> GetStatusAsync()
    {
        var (exitCode, output) = await RunGitAsync("status --short");
        return exitCode == 0 ? output : $"(git error: {output})";
    }

    /// <summary>
    /// Запустить git-команду в корне воркспейса. Таймаут: 30 секунд.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunGitAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = SelfBuildPaths.WorkspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) error.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "timeout (30s)");
        }

        var fullOutput = output.ToString() + (error.Length > 0 ? "\n" + error.ToString() : "");
        return (process.ExitCode, fullOutput);
    }
}
