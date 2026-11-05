using System.Diagnostics;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// csharp_track — указать, с каким C#-проектом работают csharp_*-тулы (фаза 2, 2026-11-05).
/// Явное указание вместо авто-дискавери: решение принимает агент (или владелец из UI).
///
/// Трек — свойство ЧАТА: хранится в csharp.json папки сессии (переживает рестарт),
/// НЕЗАВИСИМ от рабочей папки (смена root'а трек не сбрасывает). Явный трек выигрывает
/// у home-преднастройки — даже в чате с root'ом харнесса можно оттрекать внешний проект.
///
/// Принимает файл (*.slnx/*.sln/*.csproj) или папку: ровно один кандидат в папке —
/// взят, несколько — перечислены (агент выбирает файл явно), ноль — ошибка.
///
/// Загрузка воркспейса — СИНХРОННО в самом треке: осознанное действие, ждём
/// осознанно. Fail-fast: сломанный csproj узнаётся сразу, трек НЕ сохраняется.
/// </summary>
[Tool("csharp_track",
    "Point the C# tools at a project for THIS chat: track a .slnx/.sln/.csproj file (or a folder " +
    "containing exactly one of them). The track is stored in the session (survives restarts) and is " +
    "independent of the working folder; an explicit track takes precedence over the harness preset, " +
    "so it works even when the working folder is the QwenPlayground root. Use when you need csharp_* " +
    "tools on a project the preset does not cover (Unity, Godot, any .NET). The workspace is loaded " +
    "synchronously, so a broken project file is reported here and the track is not saved.",
    ToolGroup.CSharp)]
public sealed class CSharpTrackTool : AgentTool
{
    [ToolParameter("Path to a .slnx/.sln/.csproj file, or a folder containing exactly one of them (relative to the working folder or absolute)", Required = true)]
    public string Path { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.SessionDir))
        {
            return "Error: no session context — cannot save the track.";
        }

        var (file, error) = ResolveProjectFile(context, Path);
        if (error is not null)
        {
            return error;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var service = RoslynService.ForProjectFile(file!);
            var solution = await service.GetSolutionAsync(cancellationToken);
            var projectList = solution.Projects.ToArray();

            // Сохраняем только после успешной загрузки (fail-fast): список проектов —
            // из тёплого воркспейса (свеже, чем парсинг файла); файл уходит в историю
            // чата (recent) для быстрого переключения в UI-меню.
            CSharpSessionTrack.TrackProject(context.SessionDir, file!,
                projectList.Select(p => p.Name).ToArray());

            var projects = string.Join(", ", projectList.Select(p => p.Name));
            return $"Tracked {file} for this chat (saved to the session folder).\n" +
                   $"Loaded {projectList.Length} project(s) in {stopwatch.Elapsed.TotalSeconds:F1}s: {projects}.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RoslynService.ForgetProjectFile(file!);
            return $"Error: failed to load '{file}': {ex.Message}\n(the track was not saved)";
        }
    }

    /// <summary>
    /// Файл → проверить расширение; папка → кандидаты (slnx/sln/csproj в самой папке,
    /// не рекурсивно). internal — тестируется без загрузки воркспейса.
    /// </summary>
    internal static (string? File, string? Error) ResolveProjectFile(ToolContext context, string path)
    {
        // System.IO.* явно: Path/File/Directory без префикса резолвятся в свойства тула.
        var full = System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(context.ProjectRoot, path);
        if (System.IO.File.Exists(full))
        {
            var extension = System.IO.Path.GetExtension(full).ToLowerInvariant();
            return extension is ".slnx" or ".sln" or ".csproj"
                ? (full, null)
                : (null, $"Error: '{full}' is not a .slnx/.sln/.csproj file.");
        }
        if (System.IO.Directory.Exists(full))
        {
            var candidates = new List<string>();
            foreach (var name in System.IO.Directory.EnumerateFiles(full))
            {
                var extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
                if (extension is ".slnx" or ".sln" or ".csproj")
                {
                    candidates.Add(name);
                }
            }
            if (candidates.Count == 1)
            {
                return (candidates[0], null);
            }
            if (candidates.Count == 0)
            {
                return (null, $"Error: no .slnx/.sln/.csproj found in folder '{full}'.");
            }
            return (null, "Error: multiple C# project files in '" + full + "' — pass one explicitly:\n" +
                          string.Join("\n", candidates.Select(c => "  " + c)));
        }
        return (null, $"Error: path not found: {full}");
    }
}

/// <summary>
/// csharp_untrack — снять трек проекта ЧАТА (сброс к дефолту: home-преднастройка для
/// home-root'а) и освободить его Roslyn-воркспейс (после завершения вполёте-вызовов).
/// </summary>
[Tool("csharp_untrack",
    "Stop tracking the C# project of this chat (resets to the default: the QwenPlayground solution " +
    "for the harness root) and release its Roslyn workspace (frees hundreds of MB). Use when done " +
    "working on an external C# project. The working folder itself is not changed.", ToolGroup.CSharp)]
public sealed class CSharpUntrackTool : AgentTool
{
    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.SessionDir))
        {
            return Task.FromResult("Error: no session context — cannot untrack.");
        }

        var track = CSharpSessionTrack.Load(context.SessionDir);
        if (track is null)
        {
            return Task.FromResult("No C# project is tracked for this chat — nothing to untrack.");
        }
        CSharpSessionTrack.Clear(context.SessionDir);
        // Дождётся вполёте-вызовов (гейт инстанса), затем Dispose.
        RoslynService.ForgetProjectFile(track.ProjectFile);
        return Task.FromResult(
            $"Untracked {track.ProjectFile} for this chat; its Roslyn workspace was released.");
    }
}
