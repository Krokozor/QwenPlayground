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
    // агент имеет полный доступ к shell, список не блокирует ничего критичного
    // (и YoloMode авто-одобряет).
    //
    // Regex с границами СЛОВ (а не подстрока): «del » ловил «model»/«delete», «format » —
    // «formatted». Честно: коммит e3a2cf2 (2026-09-04) ЗАЯВИЛ word-boundary fix, которого
    // в diff не было (аудит 2026-10-13) — сам фикс сделан только сейчас.
    //
    // Состав — под cmd.exe (shell этого тула): del/erase (удаление файлов), rd/rmdir
    // (удаление каталогов), format (форматирование), shutdown, taskkill, diskpart,
    // remove-item (PowerShell). «rm» исключён сознательно: в cmd.exe его нет, а
    // «git rm» — частая безобидная команда (ложное срабатывание).
    private static readonly System.Text.RegularExpressions.Regex DangerousCommand = new(
        @"\b(del|erase|rd|rmdir|format|shutdown|taskkill|diskpart|remove-item)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (DangerousCommand.IsMatch(Command))
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
        // Полный вывод уходит в AgentLoop, где автокаппинг (CapToolOutput) при >8 КБ сохраняет
        // его в attachments/ сообщения и оставляет превью. Здесь не каппим: иначе полный
        // вывод теряется до того, как автокаппинг успеет его сохранить.
        return $"exit code: {process.ExitCode}\n{text}";
    }
}
