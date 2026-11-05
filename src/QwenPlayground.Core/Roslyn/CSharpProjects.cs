using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// Резолвинг C#-проекта для инструментов (фаза 2, 2026-11-05): на каком проекте работают
/// csharp_*-тулы.
///
/// Правила (дизайн владельца):
/// 1. Явный PER-CHAT трек (csharp.json в папке сессии, CSharpSessionTrack) — выигрывает
///    ВСЕГДА, независимо от root'а: трек — осознанный выбор чата, он живёт своей жизнью
///    (смена root'а трек не сбрасывает; в home-чате можно оттрекать внешний проект).
/// 2. Трека нет + root == харнесс (или подпапка харнесса) → домашний солюшен.
///    Преднастройка — дефолт на случай, если никто ничего не выбирал.
/// 3. Трека нет + root ≠ харнесс → честная ошибка. Молчаливого fallback на домашний
///    проект НЕТ: худший исход — модель думает, что смотрит чужой проект, а получает харнесс.
///
/// «Дом» — факт рантайма (SelfBuildPaths.WorkspaceRoot), не строковый литерал:
/// харнесс может лежать в любой папке.
/// </summary>
public static class CSharpProjects
{
    /// <summary>Контекст: на каком инстансе работать и откуда форматируются пути.</summary>
    public sealed record Context(RoslynService Service, string ProjectFile, bool IsHome);

    public static string HomeProjectFile => RoslynService.Shared.ProjectFile;

    /// <summary>Рабочая папка — сам харнесс или его подпапка?</summary>
    public static bool IsHomeRoot(string root)
    {
        var home = Path.GetFullPath(SelfBuildPaths.WorkspaceRoot);
        var r = Path.GetFullPath(root);
        return string.Equals(r, home, StringComparison.OrdinalIgnoreCase)
               || r.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Файл проекта — это домашний солюшен (home-фичи: XAML-индекс и т.п.)?</summary>
    public static bool IsHomeFile(string projectFile) =>
        string.Equals(Path.GetFullPath(projectFile), Path.GetFullPath(HomeProjectFile),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// null + error — работать не на чем (трек указывает на исчезнувший файл / трека нет
    /// и root не харнесс). Тул возвращает текст ошибки как есть.
    /// sessionDir — папка сессии (per-chat трек); null — проверка трека пропускается
    /// (старые вызовы, тесты).
    /// </summary>
    public static Context? Resolve(string sessionRoot, out string? error, string? sessionDir = null)
    {
        // 1. Явный per-chat трек (csharp.json) — выигрывает у преднастройки.
        if (!string.IsNullOrEmpty(sessionDir))
        {
            var track = CSharpSessionTrack.Load(sessionDir);
            if (track is not null)
            {
                if (!File.Exists(track.ProjectFile))
                {
                    error = $"The tracked C# project file no longer exists: {track.ProjectFile}. " +
                            "Re-track it with csharp_track.";
                    return null;
                }
                error = null;
                return new Context(RoslynService.ForProjectFile(track.ProjectFile),
                    track.ProjectFile, IsHomeFile(track.ProjectFile));
            }
        }

        // 2. Нет трека: home-преднастройка для папки харнесса.
        if (IsHomeRoot(sessionRoot))
        {
            error = null;
            return new Context(RoslynService.Shared, HomeProjectFile, true);
        }

        // 3. Честная ошибка — без fallback на дом.
        error = $"No C# project is tracked for this chat (working folder '{sessionRoot}'). " +
                "Use csharp_track to point the C# tools at a .slnx/.sln/.csproj file (or a folder " +
                "containing exactly one of them).";
        return null;
    }

    /// <summary>
    /// Короткое описание текущей цели (UI-поле «C#:», state-строка): трек чата (с числом
    /// проектов, если оно сохранено) → home-преднастройка → «ничего не трекается».
    /// </summary>
    public static string DescribeTarget(string? sessionRoot, string? sessionDir = null)
    {
        if (!string.IsNullOrEmpty(sessionDir))
        {
            var track = CSharpSessionTrack.Load(sessionDir);
            if (track is not null)
            {
                var count = track.Projects.Length > 0 ? $" ({track.Projects.Length} projects)" : "";
                return track.ProjectFile + count;
            }
        }

        if (string.IsNullOrWhiteSpace(sessionRoot) || IsHomeRoot(sessionRoot))
        {
            return "home (QwenPlayground solution)";
        }
        return "nothing tracked (use csharp_track)";
    }
}
