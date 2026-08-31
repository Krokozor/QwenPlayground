using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// Единая точка доступа к Roslyn-представлению солюшена QwenPlayground.
///
/// Зачем один Shared: MSBuildWorkspace очень тяжёлый (загружает весь солюшен с
/// метаданными сборок — сотни МБ), поэтому все C#-инструменты обязаны идти через
/// <see cref="Shared"/>, а не создавать свой экземпляр. Конструктор с параметром
/// оставлен для тестов и будущих сценариев с чужими солюшенами.
///
/// Инвалидация: изменение *.cs подменяет текст изменённых документов в живом
/// workspace (TryApplyChanges, без полной перезагрузки); изменение *.csproj/*.slnx
/// или провал инкрементального обновления — полное OpenSolutionAsync.
/// </summary>
public sealed class RoslynService
{
    private static bool _locatorRegistered;

    private readonly string _solutionPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private DateTime _loadedAt = DateTime.MinValue;

    public RoslynService(string? solutionPath = null)
    {
        _solutionPath = solutionPath ?? Path.Combine(SelfBuildPaths.WorkspaceRoot, "QwenPlayground.slnx");
    }

    /// <summary>Общий экземпляр для всех инструментов. Не создавать свои RoslynService в продакшн-коде.</summary>
    public static RoslynService Shared { get; } = new();

    public async Task<Solution> GetSolutionAsync(CancellationToken cancellationToken)
    {
        // Shared-синглтон может дёргаться параллельно (тесты xUnit гоняют классы параллельно,
        // будущие сценарии — тем более): без гейта два вызова одновременно ломаются на
        // _workspace.Dispose() + OpenSolutionAsync.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_solution is null || ProjectsChangedSince(_loadedAt))
            {
                await OpenAsync(cancellationToken);
                return _solution!;
            }
            if (SourcesChangedSince(_loadedAt))
            {
                if (await TryApplyIncrementalAsync(cancellationToken))
                {
                    _loadedAt = DateTime.Now;
                }
                else
                {
                    await OpenAsync(cancellationToken);
                }
            }
            return _solution!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        EnsureLocator();
        _workspace?.Dispose();
        _workspace = MSBuildWorkspace.Create();
        _solution = await _workspace.OpenSolutionAsync(_solutionPath, cancellationToken: cancellationToken);
        _loadedAt = DateTime.Now;
    }

    /// <summary>
    /// Инкрементальное обновление: подменяет текст изменившихся *.cs в живом workspace.
    /// Файл вне солюшена (например, в NekoBot/) или ошибка применения — false → полная перезагрузка.
    /// </summary>
    private async Task<bool> TryApplyIncrementalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var changed = EnumerateSourceFiles()
                .Where(file => File.GetLastWriteTime(file) > _loadedAt)
                .ToList();
            if (changed.Count == 0)
            {
                return false;
            }

            var solution = _solution!;
            foreach (var path in changed)
            {
                var document = FindDocument(solution, path);
                if (document is null)
                {
                    return false;
                }
                var text = SourceText.From(await File.ReadAllTextAsync(path, cancellationToken));
                solution = solution.WithDocumentText(document.Id, text);
            }
            if (!_workspace!.TryApplyChanges(solution))
            {
                return false;
            }
            _solution = solution;
            return true;
        }
        catch (OperationCanceledException)
        {
            // Отмена во время инкрементального обновления — не «инкремент не удался»:
            // пробрасываем, чтобы не стартовать тяжёлую полную перезагрузку workspace.
            throw;
        }
        catch
        {
            return false;
        }
    }

    internal static Document? FindDocument(Solution solution, string filePath)
    {
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is not null &&
                    document.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return document;
                }
            }
        }
        return null;
    }

    // bin/obj живут внутри каталогов проектов; остальное отсекает сам список корней скана.
    private static readonly string[] SkippedDirectoryNames = { "run", "bin", "obj", ".git", ".vs" };

    /// <summary>
    /// Каталоги солюшена — единственные, где живут его исходники. Остальной воркспейс
    /// сканировать нельзя не только из-за объёма (NekoBot — референсное приложение, ~86%
    /// всех .cs; Sandbox — песочница агента): изменение «чужого» .cs раньше давало
    /// SourcesChangedSince=true, FindDocument такой файл не находил → ложная ПОЛНАЯ
    /// перезагрузка workspace на каждый Roslyn-вызов после любого чиха в песочнице.
    /// </summary>
    private static readonly string[] SolutionSourceDirs = { "src", "tests", "tools" };

    private bool SourcesChangedSince(DateTime since) =>
        EnumerateSourceFiles().Any(file => File.GetLastWriteTime(file) > since);

    /// <summary>Изменение структуры сборки (csproj/slnx/props) требует полной перезагрузки.</summary>
    private bool ProjectsChangedSince(DateTime since) =>
        EnumerateProjectFiles().Any(file => File.GetLastWriteTime(file) > since);

    private IEnumerable<string> EnumerateSourceFiles()
    {
        var root = SelfBuildPaths.WorkspaceRoot;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs"))
        {
            yield return file;
        }
        foreach (var dir in SolutionDirectories(root))
        {
            foreach (var file in EnumerateRecursive(dir, "*.cs"))
            {
                yield return file;
            }
        }
    }

    private IEnumerable<string> EnumerateProjectFiles()
    {
        var patterns = new[] { "*.csproj", "*.slnx", "*.sln", "*.props", "*.targets", "global.json" };
        var root = SelfBuildPaths.WorkspaceRoot;
        foreach (var pattern in patterns)
        {
            foreach (var file in Directory.EnumerateFiles(root, pattern))
            {
                yield return file;
            }
        }
        foreach (var dir in SolutionDirectories(root))
        {
            foreach (var pattern in patterns)
            {
                foreach (var file in EnumerateRecursive(dir, pattern))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> SolutionDirectories(string root)
    {
        foreach (var name in SolutionSourceDirs)
        {
            var dir = Path.Combine(root, name);
            if (Directory.Exists(dir))
            {
                yield return dir;
            }
        }
    }

    private static IEnumerable<string> EnumerateRecursive(string directory, string pattern)
    {
        foreach (var file in Directory.EnumerateFiles(directory, pattern))
        {
            yield return file;
        }
        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(sub);
            if (!SkippedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var file in EnumerateRecursive(sub, pattern))
                {
                    yield return file;
                }
            }
        }
    }

    private static void EnsureLocator()
    {
        if (!_locatorRegistered)
        {
            MSBuildLocator.RegisterDefaults();
            _locatorRegistered = true;
        }
    }
}
