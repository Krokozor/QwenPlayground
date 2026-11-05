using System.Text.RegularExpressions;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// Roslyn-представление ОДНОГО C#-проекта: солюшена (*.slnx/*.sln) или отдельного
/// csproj (Godot и т.п. — проект без солюшена).
///
/// Зачем инстансов несколько (а не один на всё): MSBuildWorkspace очень тяжёлый
/// (загружает проект с метаданными сборок — сотни МБ), поэтому все C#-инструменты
/// обязаны ходить через общий кэш <see cref="ForProjectFile"/> и не создавать свои
/// экземпляры. Домашний (харнесс) инстанс — <see cref="Shared"/>: пинится, никогда
/// не Dispose'ится. Внешние (оттрекаенные через csharp_track) — кэшируются по файлу
/// проекта и освобождаются <see cref="ForgetProjectFile"/>.
///
/// Инвалидация: изменение *.cs подменяет текст изменённых документов в живом
/// workspace (TryApplyChanges, без полной перезагрузки); изменение файла проекта
/// (slnx/sln/csproj) — полное OpenAsync. Исключение: Unity перезаписывает .sln при
/// каждом domain reload — если МНОЖЕСТВО проектов в солюшене не изменилось,
/// перезагрузка не нужна (сравнение по списку путей, а не по mtime).
/// Сканирование источников — по директориям самих проектов солюшена (универсально:
/// не привязано к layout'у конкретного проекта).
/// </summary>
public sealed class RoslynService
{
    private static bool _locatorRegistered;

    // Кэш внешних инстансов: полный путь файла проекта → инстанс. Дом (Shared) в кэше
    // не живёт — он пинен и возвращается ForProjectFile напрямую.
    private static readonly Dictionary<string, RoslynService> _external =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _cacheLock = new();

    private readonly string _projectFile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private DateTime _loadedAt = DateTime.MinValue;
    // Множество путей проектов в солюшене (списком): детект «.sln перегенерирован,
    // но состав тот же» (Unity) без полной перезагрузки.
    private string? _projectSetHash;

    public RoslynService(string projectFile, bool pinned = false)
    {
        _projectFile = Path.GetFullPath(projectFile);
        Pinned = pinned;
    }

    /// <summary>Домашний инстанс (солюшен харнесса). Пинится: rebuild-пре-чек и
    /// EditDiagnostics в home-контексте ходят через него напрямую.</summary>
    public static RoslynService Shared { get; } =
        new(Path.Combine(SelfBuildPaths.WorkspaceRoot, SelfBuildPaths.SolutionFileName), pinned: true);

    /// <summary>Пинен ли инстанс (дом). Пиненные не освобождаются ForgetProjectFile.</summary>
    public bool Pinned { get; }

    /// <summary>Файл проекта, который грузит инстанс (*.slnx/*.sln/*.csproj), полный путь.</summary>
    public string ProjectFile => _projectFile;

    /// <summary>
    /// Инстанс для файла проекта (общий кэш). Домашний файл → <see cref="Shared"/>
    /// (один воркспейс на дом, не два).
    /// </summary>
    public static RoslynService ForProjectFile(string projectFile)
    {
        var full = Path.GetFullPath(projectFile);
        if (string.Equals(full, Shared.ProjectFile, StringComparison.OrdinalIgnoreCase))
        {
            return Shared;
        }
        lock (_cacheLock)
        {
            if (!_external.TryGetValue(full, out var service))
            {
                service = new RoslynService(full);
                _external[full] = service;
            }
            return service;
        }
    }

    /// <summary>
    /// Убрать инстанс из кэша и освободить воркспейс (csharp_untrack). Дождётся
    /// завершения вполёте-вызовов (гейт сериализует доступ). Дом не освобождается.
    /// </summary>
    public static void ForgetProjectFile(string projectFile)
    {
        var full = Path.GetFullPath(projectFile);
        if (string.Equals(full, Shared.ProjectFile, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        RoslynService? service;
        lock (_cacheLock)
        {
            if (!_external.TryGetValue(full, out service))
            {
                return;
            }
            _external.Remove(full);
        }
        service.ReleaseWorkspace();
    }

    public async Task<Solution> GetSolutionAsync(CancellationToken cancellationToken)
    {
        // Инстанс может дёргаться параллельно (сессии/субагенты в разных папках,
        // тесты xUnit гоняют классы параллельно): без гейта два вызова одновременно
        // ломаются на _workspace.Dispose() + OpenAsync.
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

    /// <summary>
    /// Применить externally-изменённый снапшот к живому workspace (инструменты
    /// rename/move). Вызывать ПОСЛЕ записи файлов на диск: _loadedAt=Now не даст
    /// следующему GetSolutionAsync делать лишний инкрементальный проход
    /// (файлы новее _loadedAt, но текст уже применён).
    /// </summary>
    public async Task ApplyChangesAsync(Solution solution, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_workspace is not null)
            {
                _workspace.TryApplyChanges(solution);
            }
            _solution = solution;
            _loadedAt = DateTime.Now;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Освободить воркспейс, дождавшись вполёте-вызовов (гейт). Инстанс остаётся в
    /// живом состоянии: следующий GetSolutionAsync загрузит проект заново.
    /// </summary>
    public void ReleaseWorkspace()
    {
        _gate.Wait();
        try
        {
            _workspace?.Dispose();
            _workspace = null;
            _solution = null;
            _loadedAt = DateTime.MinValue;
            _projectSetHash = null;
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
        // csproj без солюшена (Godot и т.п.): OpenProjectAsync возвращает Project —
        // берём его Solution (однопроектный) — остальной код не различает.
        if (Path.GetExtension(_projectFile).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var project = await _workspace.OpenProjectAsync(_projectFile, cancellationToken: cancellationToken);
            _solution = project.Solution;
        }
        else
        {
            _solution = await _workspace.OpenSolutionAsync(_projectFile, cancellationToken: cancellationToken);
        }
        _loadedAt = DateTime.Now;
        _projectSetHash = ComputeProjectSetHash(_solution);
    }

    /// <summary>
    /// Инкрементальное обновление: подменяет текст изменившихся *.cs в живом workspace.
    /// Файл вне проекта (например, в чужой песочнице) или ошибка применения —
    /// false → полная перезагрузка.
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
    /// Директории проектов самого солюшена — единственные, где живут его исходники.
    /// Универсальная замена старого хардкода src/tests/tools (layout харнесса):
    /// изменение «чужого» .cs вне проектов не даёт ложной полной перезагрузки.
    /// </summary>
    private IEnumerable<string> ProjectDirectories()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_solution is not null)
        {
            foreach (var project in _solution.Projects)
            {
                if (project.FilePath is not null)
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(project.FilePath));
                    if (dir is not null)
                    {
                        directories.Add(dir);
                    }
                }
            }
        }
        return directories;
    }

    private bool SourcesChangedSince(DateTime since)
    {
        if (_solution is null)
        {
            return true;
        }
        foreach (var dir in ProjectDirectories())
        {
            foreach (var file in EnumerateRecursive(dir, "*.cs"))
            {
                if (File.GetLastWriteTime(file) > since)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Изменение структуры сборки (slnx/sln/csproj) требует полной перезагрузки.</summary>
    private bool ProjectsChangedSince(DateTime since)
    {
        if (!File.Exists(_projectFile))
        {
            return true;
        }
        if (File.GetLastWriteTime(_projectFile) > since)
        {
            // Unity перегенерирует .sln при каждом domain reload: mtime меняется,
            // а состав проектов тот же — перезагрузка не нужна.
            var extension = Path.GetExtension(_projectFile).ToLowerInvariant();
            if (extension is ".sln" or ".slnx" && _solution is not null)
            {
                var hash = ComputeProjectSetHashFromDisk(_projectFile);
                if (hash is not null && hash == _projectSetHash)
                {
                    // состав не изменился — дальше проверяем csproj'ы проектов
                }
                else
                {
                    return true;
                }
            }
            else
            {
                return true;
            }
        }
        if (_solution is null)
        {
            return true;
        }
        foreach (var project in _solution.Projects)
        {
            var path = project.FilePath;
            if (path is not null && File.Exists(path) && File.GetLastWriteTime(path) > since)
            {
                return true;
            }
            // Directory.Build.props/targets в директории проекта (общие свойства сборки).
            var dir = Path.GetDirectoryName(Path.GetFullPath(path ?? string.Empty));
            if (dir is not null)
            {
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
                {
                    var propsPath = Path.Combine(dir, name);
                    if (File.Exists(propsPath) && File.GetLastWriteTime(propsPath) > since)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private IEnumerable<string> EnumerateSourceFiles()
    {
        foreach (var dir in ProjectDirectories())
        {
            foreach (var file in EnumerateRecursive(dir, "*.cs"))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// «Отпечаток» множества проектов: списком путей (из живого Solution или из
    /// текста солюшена на диске). Сравнение отпечатков вместо mtime — защита от
    /// перегенерации .sln (Unity): файл новый, а состав тот же.
    /// </summary>
    private static string? ComputeProjectSetHash(Solution solution)
    {
        var paths = solution.Projects
            .Select(p => p.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        return string.Join(';', paths);
    }

    private static string? ComputeProjectSetHashFromDisk(string solutionFile)
    {
        try
        {
            var text = File.ReadAllText(solutionFile);
            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (solutionFile.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                // <Project Path="src/Foo/Foo.csproj" /> — значение Path="...".
                // IndexOf вместо regex: вербати-литерал с тремя кавычками — ловушка
                // при ручном экранировании (инцидент 2026-11-04: потерянная кавычка
                // рвала весь файл).
                const string marker = "Path=\"";
                var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                while (start >= 0)
                {
                    start += marker.Length;
                    var end = text.IndexOf('"', start);
                    if (end < 0)
                    {
                        break;
                    }
                    paths.Add(text.Substring(start, end - start));
                    start = text.IndexOf(marker, end, StringComparison.OrdinalIgnoreCase);
                }
            }
            else
            {
                // Project("...") = "Name", "path.csproj", "{GUID}" — путь к csproj
                // (цитатами не ловим: вербати-экранирование кавычек хрупко).
                foreach (Match match in Regex.Matches(text, @"[^\s""\\]+\.csproj"))
                {
                    paths.Add(match.Value);
                }
            }
            return string.Join(';', paths);
        }
        catch
        {
            return null;
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
