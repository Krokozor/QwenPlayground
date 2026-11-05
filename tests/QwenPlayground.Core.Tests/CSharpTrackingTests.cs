using QwenPlayground.Core.Roslyn;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Трекинг C#-проектов (фаза 2, 2026-11-05): per-chat трек (CSharpSessionTrack,
/// csharp.json в папке сессии), резолвер «трек → home-преднастройка → честная ошибка»,
/// кандидаты csharp_track, статичная строка полки.
/// </summary>
public sealed class CSharpTrackingTests
{
    // ─────────────────────────── CSharpSessionTrack (per-chat) ───────────────────────────

    [Fact]
    public void SessionTrack_SaveLoadClear_RoundTrip()
    {
        var dir = TempDir("track-");
        try
        {
            Assert.Null(CSharpSessionTrack.Load(dir)); // пустая папка — трека нет

            CSharpSessionTrack.Save(dir, "V:\\Projects\\Game\\Game.sln", new[] { "Game", "Game.Editor" });
            var track = CSharpSessionTrack.Load(dir);
            Assert.NotNull(track);
            Assert.Equal("V:\\Projects\\Game\\Game.sln", track!.ProjectFile);
            Assert.Equal(new[] { "Game", "Game.Editor" }, track.Projects);

            CSharpSessionTrack.Clear(dir);
            Assert.Null(CSharpSessionTrack.Load(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SessionTrack_CorruptedFile_NoTrack()
    {
        var dir = TempDir("track-corrupt-");
        try
        {
            File.WriteAllText(CSharpSessionTrack.FilePath(dir), "{ это не JSON");
            Assert.Null(CSharpSessionTrack.Load(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SessionTrack_ParseProjects_SlnxSlnCsproj()
    {
        var dir = TempDir("parse-");
        try
        {
            var slnx = Path.Combine(dir, "Game.slnx");
            File.WriteAllText(slnx,
                "<Solution>\n  <Folder Name=\"/src/\">\n" +
                "    <Project Path=\"src/Game/Game.csproj\" Name=\"Game\" />\n" +
                "    <Project Path=\"src/Game.Editor/Game.Editor.csproj\" Name=\"Game.Editor\" />\n" +
                "  </Folder>\n</Solution>");
            Assert.Equal(new[] { "Game", "Game.Editor" }, CSharpSessionTrack.ParseProjects(slnx));

            var sln = Path.Combine(dir, "Game2.sln");
            File.WriteAllText(sln,
                "Microsoft Visual Studio Solution File, format version 12.00\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Core\", \"src\\Core\\Core.csproj\", " +
                "\"{11111111-1111-1111-1111-111111111111}\"\n" +
                "EndProject\n");
            Assert.Equal(new[] { "Core" }, CSharpSessionTrack.ParseProjects(sln));

            var csproj = Path.Combine(dir, "Solo.csproj");
            File.WriteAllText(csproj, "<Project />");
            Assert.Equal(new[] { "Solo" }, CSharpSessionTrack.ParseProjects(csproj));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ─────────────────────────── CSharpProjects (резолвер) ───────────────────────────

    [Fact]
    public void IsHomeRoot_HomeAndSubfolders()
    {
        Assert.True(CSharpProjects.IsHomeRoot(SelfBuildPaths.WorkspaceRoot));
        Assert.True(CSharpProjects.IsHomeRoot(Path.Combine(SelfBuildPaths.WorkspaceRoot, "src")));
        Assert.True(CSharpProjects.IsHomeRoot(Path.Combine(SelfBuildPaths.WorkspaceRoot, "src", "QwenPlayground.Core")));
        // Сосед и предок — не дом.
        Assert.False(CSharpProjects.IsHomeRoot(Path.Combine(SelfBuildPaths.WorkspaceRoot, "..", "QwenPlayground-Other")));
        Assert.False(CSharpProjects.IsHomeRoot(Path.GetDirectoryName(SelfBuildPaths.WorkspaceRoot)!));
    }

    [Fact]
    public void Resolve_HomeRoot_ReturnsHomePreset()
    {
        var context = CSharpProjects.Resolve(SelfBuildPaths.WorkspaceRoot, out var error);
        Assert.Null(error);
        Assert.NotNull(context);
        Assert.True(context!.IsHome);
        Assert.Same(RoslynService.Shared, context.Service);
    }

    [Fact]
    public void Resolve_UntrackedRoot_HonestError()
    {
        var root = TempDir("untracked-");
        try
        {
            var context = CSharpProjects.Resolve(root, out var error);
            Assert.Null(context);
            Assert.Contains("csharp_track", error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_TrackedSession_ReturnsTrackedProject()
    {
        var root = TempDir("tracked-root-");
        var sessionDir = TempDir("tracked-session-");
        var projectFile = Path.Combine(root, "Game.csproj");
        File.WriteAllText(projectFile, "<Project />");
        try
        {
            CSharpSessionTrack.Save(sessionDir, projectFile, Array.Empty<string>());
            var context = CSharpProjects.Resolve(root, out var error, sessionDir);
            Assert.Null(error);
            Assert.NotNull(context);
            Assert.False(context!.IsHome);
            Assert.Equal(Path.GetFullPath(projectFile), context.ProjectFile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_TrackedFileMissing_HonestError()
    {
        var root = TempDir("missing-root-");
        var sessionDir = TempDir("missing-session-");
        try
        {
            CSharpSessionTrack.Save(sessionDir, Path.Combine(root, "gone.csproj"), Array.Empty<string>());
            var context = CSharpProjects.Resolve(root, out var error, sessionDir);
            Assert.Null(context);
            Assert.Contains("no longer exists", error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_SessionTrack_BeatsHomePreset()
    {
        // Решение владельца (2026-11-05): явный трек выигрывает у home-преднастройки —
        // даже в чате с root'ом харнесса можно оттрекать внешний проект.
        var sessionDir = TempDir("home-track-");
        var projectFile = Path.Combine(sessionDir, "External.csproj");
        File.WriteAllText(projectFile, "<Project />");
        try
        {
            CSharpSessionTrack.Save(sessionDir, projectFile, Array.Empty<string>());
            var context = CSharpProjects.Resolve(SelfBuildPaths.WorkspaceRoot, out var error, sessionDir);
            Assert.Null(error);
            Assert.NotNull(context);
            Assert.False(context!.IsHome);
            Assert.Equal(Path.GetFullPath(projectFile), context.ProjectFile);
        }
        finally
        {
            Directory.Delete(sessionDir, recursive: true);
        }
    }

    // ─────────────────────────── csharp_track: кандидаты ───────────────────────────

    private static ToolContext TempContext() => new(Path.GetTempPath());

    [Fact]
    public void Track_ResolveProjectFile_SingleCandidateInFolder()
    {
        var dir = TempDir("track-");
        var sln = Path.Combine(dir, "Game.sln");
        File.WriteAllText(sln, "");
        File.WriteAllText(Path.Combine(dir, "README.md"), "not a candidate");
        try
        {
            var (file, error) = CSharpTrackTool.ResolveProjectFile(TempContext(), dir);
            Assert.Null(error);
            Assert.Equal(Path.GetFullPath(sln), file);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Track_ResolveProjectFile_MultipleCandidates_Listed()
    {
        var dir = TempDir("track-");
        File.WriteAllText(Path.Combine(dir, "a.sln"), "");
        File.WriteAllText(Path.Combine(dir, "b.csproj"), "");
        try
        {
            var (file, error) = CSharpTrackTool.ResolveProjectFile(TempContext(), dir);
            Assert.Null(file);
            Assert.Contains("a.sln", error);
            Assert.Contains("b.csproj", error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Track_ResolveProjectFile_NoCandidates_Error()
    {
        var dir = TempDir("track-");
        try
        {
            var (file, error) = CSharpTrackTool.ResolveProjectFile(TempContext(), dir);
            Assert.Null(file);
            Assert.Contains("no .slnx/.sln/.csproj", error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Track_ResolveProjectFile_FileWithWrongExtension_Error()
    {
        var dir = TempDir("track-");
        var md = Path.Combine(dir, "notes.md");
        File.WriteAllText(md, "");
        try
        {
            var (file, error) = CSharpTrackTool.ResolveProjectFile(TempContext(), md);
            Assert.Null(file);
            Assert.Contains("not a .slnx/.sln/.csproj", error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Track_ResolveProjectFile_RelativePath_ResolvesAgainstRoot()
    {
        var dir = TempDir("track-");
        var csproj = Path.Combine(dir, "Game.csproj");
        File.WriteAllText(csproj, "");
        try
        {
            var context = new ToolContext(dir);
            var (file, error) = CSharpTrackTool.ResolveProjectFile(context, "Game.csproj");
            Assert.Null(error);
            Assert.Equal(Path.GetFullPath(csproj), file);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Track_ResolveProjectFile_MissingPath_Error()
    {
        var (file, error) = CSharpTrackTool.ResolveProjectFile(TempContext(), "no-such-dir-xyz/Nope.csproj");
        Assert.Null(file);
        Assert.Contains("path not found", error);
    }

    // ─────────────────── Строка полки csharp — статичная (решение 2026-11-05) ───────────────────
    // Живой трек НЕ выводится в системный промпт (тулы не должны инвалидировать KV-кеш):
    // текущий трек виден в <state>-блоке при активной полке. Тесты — регрессия на статичность.

    [Fact]
    public void Shelf_CSharpRow_Is_Static()
    {
        // Реестр по Core-сборке: реальные [Tool]-классы, csharp_symbol — из группы CSharp.
        var registry = new QwenPlayground.Core.Tools.ToolRegistry();
        var index = QwenPlayground.Core.Tools.ToolGroupIndex.Render(
            new[] { QwenPlayground.Core.Chat.ToolGroup.CSharp }, registry);
        Assert.Contains("csharp_symbol", index);
        // Строка не несёт живое состояние сессии (текущий трек) — только описание системы.
        Assert.DoesNotContain("C# target", index);
    }

    [Fact]
    public void Shelf_CSharpRow_Inactive_Static_Description()
    {
        var registry = new QwenPlayground.Core.Tools.ToolRegistry();
        var index = QwenPlayground.Core.Tools.ToolGroupIndex.Render(
            Array.Empty<QwenPlayground.Core.Chat.ToolGroup>(), registry);
        // Описание неактивной полки — про per-chat трекинг и state-блок.
        Assert.Contains("csharp_track / csharp_untrack", index);
        Assert.Contains("<state>", index);
        Assert.DoesNotContain("C# target", index);
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static string TempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "qpw-" + prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
