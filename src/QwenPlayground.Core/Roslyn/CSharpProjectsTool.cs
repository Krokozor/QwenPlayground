using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// csharp_projects — список проектов текущего трека ЧАТА (фаза 2, 2026-11-05).
/// Источник — csharp.json сессии + дешёвый живой парсинг самого файла солюшена
/// (текст, без загрузки воркспейса — холодная загрузка ради списка запрещена).
/// </summary>
[Tool("csharp_projects",
    "List the projects of the C# project currently tracked for this chat (read from the session's " +
    "csharp.json plus a cheap text parse of the solution file — no workspace load). Use to see what " +
    "the csharp_* tools can see before loading the workspace.", ToolGroup.CSharp)]
public sealed class CSharpProjectsTool : AgentTool
{
    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.SessionDir))
        {
            return Task.FromResult("Error: no session context — cannot read the track.");
        }

        var track = CSharpSessionTrack.Load(context.SessionDir);
        if (track is null)
        {
            return Task.FromResult(CSharpProjects.IsHomeRoot(context.ProjectRoot)
                ? "No explicit track for this chat — the C# tools operate on the QwenPlayground " +
                  "solution (harness preset). Use csharp_track to point them at another project."
                : "No C# project is tracked for this chat. Use csharp_track to point the C# tools " +
                  "at a .slnx/.sln/.csproj file (or a folder containing exactly one of them).");
        }

        // Живой парсинг файла (дешёвый, текст) — свежее сохранённого списка; fallback — список
        // из csharp.json (заполняется на csharp_track из тёплого воркспейса).
        var fresh = CSharpSessionTrack.ParseProjects(track.ProjectFile);
        var names = fresh.Length > 0 ? fresh : track.Projects;

        return Task.FromResult(
            $"Tracked for this chat: {track.ProjectFile}\n" +
            $"Projects ({names.Length}): {string.Join(", ", names)}");
    }
}
