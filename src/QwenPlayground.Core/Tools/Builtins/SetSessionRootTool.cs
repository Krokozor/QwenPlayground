namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Сменить рабочую папку ТЕКУЩЕЙ сессии (root инструментов, план 2026-10-01):
/// со следующей итерации все path-тулы и cwd shell работают вокруг новой папки,
/// системный промпт покажет её в блоке «Твоя сессия». Валидация — у сессии
/// (ToolContext.SetSessionRoot): абсолютный путь, папка существует; тул папки
/// НЕ создаёт. Ошибка — root не меняется. Глобальная настройка ProjectRoot —
/// только дефолт; здесь решается сессия.
/// </summary>
[Tool("set_session_root",
    "Change the working folder of THIS chat session: from the next step all file tools " +
    "(relative paths) and the shell cwd operate around the new folder, and the system prompt " +
    "will show it. The folder must already exist — this tool does NOT create folders. " +
    "Use when the task switches to another project/folder: say it out loud, then call this. " +
    "Pass an absolute path.")]
public sealed class SetSessionRootTool : AgentTool
{
    [ToolParameter(
        "Absolute path of the new working folder (must exist; e.g. V:\\Projects\\Game). Relative paths are rejected.",
        Required = true)]
    public string Path { get; set; } = string.Empty;

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.SetSessionRoot is null)
        {
            return Task.FromResult(
                "Error: set_session_root is unavailable in this context (no session to bind the folder to).");
        }
        var error = context.SetSessionRoot(Path);
        return Task.FromResult(error is null
            ? $"Working folder of this session is now: {Path.Trim()}. From the next step all file tools " +
              "(relative paths) and the shell cwd operate around it."
            : $"Error: {error}");
    }
}
