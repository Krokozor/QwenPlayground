namespace QwenPlayground.Core.SelfBuild;

/// <summary>
/// Пути развёртывания и самосборки. Корень воркспейса определяется так:
/// 1) переменная окружения QWENPLAYGROUND_ROOT (ставит лаунчер при запуске);
/// 2) подъём вверх от AppContext.BaseDirectory до каталога с QwenPlayground.slnx;
/// 3) текущий рабочий каталог (fallback для dev-запуска из IDE).
/// </summary>
public static class SelfBuildPaths
{
    public const string SolutionFileName = "QwenPlayground.slnx";

    /// <summary>Имя каталога внешних инструментов (ffmpeg и т.п.), управляемых лаунчером.</summary>
    public const string ExternalDirName = "external";

    private static readonly string? _workspaceRootOverride;

    static SelfBuildPaths()
    {
        var fromEnv = Environment.GetEnvironmentVariable("QWENPLAYGROUND_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
        {
            _workspaceRootOverride = Path.GetFullPath(fromEnv);
        }
    }

    public static string WorkspaceRoot =>
        _workspaceRootOverride
        ?? LocateFromBaseDirectory()
        ?? Environment.CurrentDirectory;

    /// <summary>
    /// Каталог внешних инструментов (ffmpeg и т.п.). Лаунчер ставит
    /// QWENPLAYGROUND_EXTERNAL_DIR при запуске; фолбэк — &lt;workspaceRoot&gt;/external.
    /// </summary>
    public static string ExternalDir
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("QWENPLAYGROUND_EXTERNAL_DIR");
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return Path.GetFullPath(fromEnv);
            }
            return Path.Combine(WorkspaceRoot, ExternalDirName);
        }
    }

    public static string RunRoot => Path.Combine(WorkspaceRoot, "run");

    // Лаунчер живёт в отдельном каталоге (сиблинг run/), а не в корне run/:
    // build с -o применяется ко всему графу (лаунчер ссылается на Core), и повторная
    // сборка Core из общего obj\Release чистит Core.dll в только что собранной папке версии.
    public static string LauncherDir => Path.Combine(WorkspaceRoot, "launcher");

    // Pointer-layout: версии — неизменяемые каталоги run/<id>, активная версия — current.txt.
    // Каталоги не удаляются/не переименовываются под работающим процессом (Windows-локи).
    // (Legacy-layout run/current + run/next + run/backup выведен из использования и удалён.)
    public static string CurrentPointerFile => Path.Combine(RunRoot, "current.txt");
    public static string VersionDir(string id) => Path.Combine(RunRoot, id);

    public static string JournalFile => Path.Combine(RunRoot, "journal.json");
    public static string RestartRequestFile => Path.Combine(RunRoot, "restart.request");
    public static string AppProject => Path.Combine(WorkspaceRoot, @"src\QwenPlayground.App\QwenPlayground.App.csproj");
    public static string TestProject => Path.Combine(WorkspaceRoot, @"tests\QwenPlayground.Core.Tests\QwenPlayground.Core.Tests.csproj");

    private static string? LocateFromBaseDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>
    /// Я ли запущен из развёрнутого layout'а? Поддерживаются оба:
    /// legacy — BaseDirectory == run/current; pointer — run/&lt;id&gt; и current.txt == &lt;id&gt;.
    /// </summary>
    public static bool TryGetDeployedRunRoot(out string runRoot)
    {
        runRoot = string.Empty;
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var dirName = Path.GetFileName(baseDir);
        var parent = Path.GetDirectoryName(baseDir);
        if (parent is null || !File.Exists(Path.Combine(parent, "journal.json")))
        {
            return false;
        }
        if (string.Equals(dirName, "current", StringComparison.OrdinalIgnoreCase))
        {
            runRoot = parent;
            return true;
        }
        var pointerFile = Path.Combine(parent, "current.txt");
        if (File.Exists(pointerFile) &&
            string.Equals(File.ReadAllText(pointerFile).Trim(), dirName, StringComparison.OrdinalIgnoreCase))
        {
            runRoot = parent;
            return true;
        }
        return false;
    }
}
