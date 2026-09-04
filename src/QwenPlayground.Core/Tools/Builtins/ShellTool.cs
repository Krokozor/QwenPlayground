using System.Diagnostics;
using System.Text;

namespace QwenPlayground.Core.Tools.Builtins;

[Tool("shell", "Run a shell command in the project root (cmd.exe /c). Returns exit code and output.")]
public sealed class ShellTool : AgentTool
{
    [ToolParameter("Command to execute", Required = true)]
    public string Command { get; set; } = string.Empty;

    [ToolParameter("Timeout in seconds")]
    public int TimeoutSeconds { get; set; } = 60;

    // Подсказка для подтверждения пользователя (TryConfirm), НЕ граница безопасности:
    // агент имеет полный доступ к shell, список не блокирует ничего критичного.
    private static readonly string[] DangerousPatterns =
        ["del ", "rmdir", "rm -", "format ", "shutdown", "taskkill", "remove-item"];

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var lower = Command.ToLowerInvariant();
        if (DangerousPatterns.Any(lower.Contains))
        {
            var pendingConfirm = context.Scope.TryConfirm(
                $"Агент хочет выполнить потенциально опасную команду:\n{Command}", cancellationToken);
            if (pendingConfirm is null)
            {
                return "Error: dangerous command requires user confirmation, which is unavailable in this context";
            }
            if (!await pendingConfirm)
            {
                return "Error: user rejected the command";
            }
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + Command,
            WorkingDirectory = context.ProjectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(Math.Max(TimeoutSeconds, 1)), cancellationToken);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            return $"Error: timeout after {TimeoutSeconds}s";
        }
        catch (OperationCanceledException)
        {
            // Ход отменён пользователем: без Kill процесс-сирота (например, недобитый dotnet build)
            // продолжал бы работать после остановки агента.
            process.Kill(entireProcessTree: true);
            throw;
        }

        var text = output.ToString();
        const int cap = 8000;
        if (text.Length > cap)
        {
            text = text[..cap] + "\n... (output truncated)";
        }
        return $"exit code: {process.ExitCode}\n{text}";
    }
}
