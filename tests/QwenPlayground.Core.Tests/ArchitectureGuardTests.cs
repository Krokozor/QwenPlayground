using System.Text.RegularExpressions;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Архитектурный страж: граница Core/App — структурная. Core таргетит net10.0 (не
/// -windows), поэтому WPF/WinForms ему физически недоступны (сборка упадёт), а
/// ProjectReference Core → App — цикл (сборка упадёт). Билд это и так держит;
/// тест страхует от «помощного» регресса: перетаргет Core на net10.0-windows или
/// добавление FrameworkReference Microsoft.WindowsDesktop.App ради «починить сборку».
/// Тогда весь домен станет несносимым без Windows-UI, и квест сегрегации провалится.
/// </summary>
public sealed class ArchitectureGuardTests
{
    private static string CoreCsproj()
    {
        var path = Path.Combine(SelfBuildPaths.WorkspaceRoot, "src", "QwenPlayground.Core", "QwenPlayground.Core.csproj");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Core_Tfm_IsNotWindows()
    {
        var text = CoreCsproj();
        var match = Regex.Match(text, @"<TargetFramework>\s*([^<]+?)\s*</TargetFramework>");
        Assert.True(match.Success, "TargetFramework не найден в Core.csproj");
        Assert.DoesNotContain("windows", match.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Core_HasNoDesktopUiFramework()
    {
        var text = CoreCsproj();
        Assert.DoesNotContain("Microsoft.WindowsDesktop.App", text);
        Assert.DoesNotContain("UseWPF", text);
        Assert.DoesNotContain("UseWindowsForms", text);
    }

    [Fact]
    public void Core_DoesNotReferenceApp()
    {
        var text = CoreCsproj();
        Assert.DoesNotContain("QwenPlayground.App", text);
    }

    [Fact]
    public void ViewModels_DoNotConstructCoreStores()
    {
        // Фаза 3 (план 2026-09-28): Core-сторы (память, слои, сессии, KV) владеет
        // композиционный корень (Main) — VM получают общие экземпляры. Новый экземпляр
        // в VM = второй писатель в тот же файл (double-writer layers.json: компакция
        // перезаписывала правки вкладки «Суммаризация») или невидимое дублирующее
        // состояние. Тест, который поймал бы это на ревью, а не в проде.
        var dir = Path.Combine(SelfBuildPaths.WorkspaceRoot, "src", "QwenPlayground.App", "ViewModels");
        Assert.True(Directory.Exists(dir), $"каталог ViewModels не найден: {dir}");
        var forbidden = new[]
        {
            "new MemoryStore(",
            "new MemoryLayerStore(",
            "new SessionStore(",
            "new KvCacheController("
        };
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in forbidden)
            {
                if (text.Contains(pattern))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {pattern}");
                }
            }
        }
        Assert.Empty(offenders);
    }
}
