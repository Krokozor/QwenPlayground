using QwenPlayground.Core.Roslyn;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Трекинг C#-проектов (2026-11-04): хранилище пометок (C1), резолвер
/// «рабочая папка → проект» (C3), кандидаты csharp_track (C4).
/// </summary>
public sealed class CSharpTrackingTests
{
    // ─────────────────────────── CSharpProjectStore ───────────────────────────

    [Fact]
    public void Store_SetGetRemove_RoundTrip()
    {
        var store = NewStore();
        store.Set("V:\\Projects\\Game", "V:\\Projects\\Game\\Game.sln");

        Assert.Equal("V:\\Projects\\Game\\Game.sln", store.Get("V:\\Projects\\Game"));
        // Регистр и хвостовой сепаратор — одна и та же папка.
        Assert.Equal("V:\\Projects\\Game\\Game.sln", store.Get(@"v:\projects\game\"));

        Assert.True(store.Remove("V:\\Projects\\Game"));
        Assert.Null(store.Get("V:\\Projects\\Game"));
        Assert.False(store.Remove("V:\\Projects\\Game")); // второй раз — её уже нет
    }

    [Fact]
    public void Store_PersistsAcrossInstances()
    {
        var file = TempFile("store.json");
        var store = new CSharpProjectStore(file);
        store.Set("D:\\Work", "D:\\Work\\Work.csproj");

        var reloaded = new CSharpProjectStore(file);
        Assert.Equal("D:\\Work\\Work.csproj", reloaded.Get("D:\\Work"));
    }

    [Fact]
    public void Store_CorruptedFile_StartsEmpty()
    {
        var file = TempFile("store-corrupt.json");
        File.WriteAllText(file, "{ это не JSON");

        var store = new CSharpProjectStore(file);
        Assert.Empty(store.All());
    }

    [Fact]
    public void Store_All_OrderedAndComplete()
    {
        var store = NewStore();
        store.Set("B:\\Second", "B:\\Second\\s.slnx");
        store.Set("A:\\First", "A:\\First\\s.sln");

        var all = store.All();
        Assert.Equal(2, all.Count);
        Assert.Equal("A:\\First", all[0].Root);
        Assert.Equal("B:\\Second", all[1].Root);
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
        var root = Path.Combine(Path.GetTempPath(), "qpw-untracked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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
    public void Resolve_TrackedRoot_ReturnsTrackedProject()
    {
        // Уникальный root + dummy-файл проекта: Shared-хранилище не трогаем чужими ключами.
        var root = Path.Combine(Path.GetTempPath(), "qpw-tracked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var projectFile = Path.Combine(root, "Game.csproj");
        File.WriteAllText(projectFile, "<Project />");
        try
        {
            CSharpProjectStore.Shared.Set(root, projectFile);
            var context = CSharpProjects.Resolve(root, out var error);
            Assert.Null(error);
            Assert.NotNull(context);
            Assert.False(context!.IsHome);
            Assert.Equal(Path.GetFullPath(projectFile), context.ProjectFile);
        }
        finally
        {
            CSharpProjectStore.Shared.Remove(root);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_TrackedFileMissing_HonestError()
    {
        var root = Path.Combine(Path.GetTempPath(), "qpw-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CSharpProjectStore.Shared.Set(root, Path.Combine(root, "gone.csproj"));
            var context = CSharpProjects.Resolve(root, out var error);
            Assert.Null(context);
            Assert.Contains("no longer exists", error);
        }
        finally
        {
            CSharpProjectStore.Shared.Remove(root);
            Directory.Delete(root, recursive: true);
        }
    }

    // ─────────────────────────── csharp_track: кандидаты ───────────────────────────

    private static ToolContext TempContext() => new(Path.GetTempPath());

    [Fact]
    public void Track_ResolveProjectFile_SingleCandidateInFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qpw-track-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
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
        var dir = Path.Combine(Path.GetTempPath(), "qpw-track-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
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
        var dir = Path.Combine(Path.GetTempPath(), "qpw-track-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
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
        var dir = Path.Combine(Path.GetTempPath(), "qpw-track-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
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
        var dir = Path.Combine(Path.GetTempPath(), "qpw-track-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
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

    private static CSharpProjectStore NewStore() => new(TempFile("store-" + Guid.NewGuid().ToString("N") + ".json"));

    private static string TempFile(string name) => Path.Combine(Path.GetTempPath(), "qpw-" + name);
}
