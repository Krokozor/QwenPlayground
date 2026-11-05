using System.Diagnostics;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// csharp_track — указать, с каким C#-проектом работают csharp_*-тулы (трекинг,
/// 2026-11-04). Явное указание вместо авто-дискавери: решение принимает агент (или
/// владелец из UI — то же хранилище), система не гадает.
///
/// Принимает файл (*.slnx/*.sln/*.csproj) или папку: ровно один кандидат в папке —
/// взят, несколько — перечислены (агент выбирает файл явно), ноль — ошибка.
///
/// Загрузка воркспейса — СИНХРОННО в самом треке: осознанное действие, ждём
/// осознанно. Fail-fast: сломанный csproj узнаётся сразу, пометка НЕ сохраняется.
/// </summary>
[Tool("csharp_track",
    "Point the C# tools at a project: track a .slnx/.sln/.csproj file (or a folder containing " +
    "exactly one of them) for the current working folder. Use when the working folder is NOT the " +
    "QwenPlayground harness root and you need csharp_* tools on an external project (Unity, Godot, " +
    "any .NET). The workspace is loaded synchronously, so a broken project file is reported here " +
    "and the track is not saved. The tracking persists across sessions for the same working folder.",
    ToolGroup.CSharp)]
public sealed class CSharpTrackTool : AgentTool
{
    [ToolParameter("Path to a .slnx/.sln/.csproj file, or a folder containing exactly one of them (relative to the working folder or absolute)", Required = true)]
    public string Path { get; set; } = string.Empty;

    [ToolParameter("Optional working folder to bind the track to (default: the current session's working folder)", Required = false)]
    public string? Root { get; set; }

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var root = System.IO.Path.GetFullPath(string.IsNullOrWhiteSpace(Root) ? context.ProjectRoot : Root);
        if (CSharpProjects.IsHomeRoot(root))
        {
            return "The working folder is the QwenPlayground harness root — the C# tools already " +
                   "operate on the QwenPlayground solution by preset. Nothing to track.";
        }

        var (file, error) = ResolveProjectFile(context, Path);
        if (error is not null)
        {
            return error;
        }

        CSharpProjectStore.Shared.Set(root, file!);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var service = RoslynService.ForProjectFile(file!);
            var solution = await service.GetSolutionAsync(cancellationToken);
            var projectList = solution.Projects.ToArray();
            var projects = string.Join(", ", projectList.Select(p => p.Name));
            return $"Tracked {file} for working folder {root}.\n" +
                   $"Loaded {projectList.Length} project(s) in {stopwatch.Elapsed.TotalSeconds:F1}s: {projects}.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail-fast: сломанный проект не оставляем в помеченном состоянии.
            CSharpProjectStore.Shared.Remove(root);
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
/// csharp_untrack — снять пометку проекта для рабочей папки и освободить его
/// Roslyn-воркспейс (после завершения вполёте-вызовов).
/// </summary>
[Tool("csharp_untrack",
    "Stop tracking the C# project of the current working folder and release its Roslyn workspace " +
    "(frees hundreds of MB). Use when done working on an external C# project. The working folder " +
    "itself is not changed.", ToolGroup.CSharp)]
public sealed class CSharpUntrackTool : AgentTool
{
    [ToolParameter("Optional working folder to untrack (default: the current session's working folder)", Required = false)]
    public string? Root { get; set; }

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var root = System.IO.Path.GetFullPath(string.IsNullOrWhiteSpace(Root) ? context.ProjectRoot : Root);
        var file = CSharpProjectStore.Shared.Get(root);
        if (file is null)
        {
            return Task.FromResult(
                $"No C# project is tracked for working folder '{root}' — nothing to untrack.");
        }
        CSharpProjectStore.Shared.Remove(root);
        // Дождётся вполёте-вызовов (гейт инстанса), затем Dispose.
        RoslynService.ForgetProjectFile(file);
        return Task.FromResult(
            $"Untracked {file} from working folder {root}; its Roslyn workspace was released.");
    }
}
