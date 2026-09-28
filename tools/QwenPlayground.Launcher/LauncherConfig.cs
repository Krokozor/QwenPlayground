using System.IO;
using System.Text.Json;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Launcher;

/// <summary>
/// Конфигурация лаунчера. Файл: launcher.json в корне воркспейса.
/// Определяет репозиторий, ветку и инструменты для управления.
/// </summary>
public sealed class LauncherConfig
{
    /// <summary>Корень проекта (где .slnx). Если не задан — вычисляется из расположения лаунчера.</summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>
    /// URL git-репозитория. Пусто — лаунчер подтягивает из `git remote get-url origin`
    /// (важно для форков: каждый форк видит свой origin, а не URL владельца).
    /// </summary>
    public string Repo { get; set; } = string.Empty;

    /// <summary>Ветка для синхронизации.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>Дополнительные рабочие папки (агент может работать и с ними).</summary>
    public List<string> AdditionalWorkspaces { get; set; } = new();

    /// <summary>Инструменты для управления (ffmpeg и др.).</summary>
    public Dictionary<string, ToolConfig> Tools { get; set; } = new();

    /// <summary>Имя каталога внешних инструментов (относительно корня воркспейса).</summary>
    public string ExternalDir { get; set; } = SelfBuildPaths.ExternalDirName;

    /// <summary>Абсолютный путь к каталогу внешних инструментов.</summary>
    public string ExternalDirPath =>
        Path.Combine(SelfBuildPaths.WorkspaceRoot, ExternalDir.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Эффективный корень проекта: из конфига или вычисленный из расположения лаунчера.
    /// Лаунчер живёт в &lt;workspaceRoot&gt;/launcher/, значит корень = родитель.
    /// </summary>
    public string EffectiveWorkspaceRoot
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(WorkspaceRoot) && Directory.Exists(WorkspaceRoot))
            {
                return Path.GetFullPath(WorkspaceRoot);
            }
            // Вычисляем: лаунчер в &lt;root&gt;/launcher/ → корень = родитель
            var launcherDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var parent = Path.GetDirectoryName(launcherDir);
            if (parent is not null && File.Exists(Path.Combine(parent, "QwenPlayground.slnx")))
            {
                return parent;
            }
            return launcherDir;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Путь к файлу конфига: launcher.json в корне воркспейса.</summary>
    public static string ConfigPath => Path.Combine(SelfBuildPaths.WorkspaceRoot, "launcher.json");

    /// <summary>Загрузить конфиг. Если файла нет — создать дефолтный.</summary>
    public static LauncherConfig Load()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions) ?? CreateDefault();
            }
            catch
            {
                // Повреждённый конфиг — создаём дефолтный
            }
        }
        return CreateDefault();
    }

    /// <summary>Сохранить конфиг в файл.</summary>
    public void Save()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    /// <summary>Создать дефолтный конфиг и сохранить его.</summary>
    public static LauncherConfig CreateDefault()
    {
        var config = new LauncherConfig
        {
            // Repo намеренно пуст: подтягивается из origin (форки видят свой remote).
            Branch = "main",
            Tools = new Dictionary<string, ToolConfig>
            {
                // ffmpeg: тег релиза — всегда «latest» (BtbN перезаписывает его на месте),
                // имя ассета постоянное, поэтому шаблон не нужен. Верхний каталог архива
                // (ffmpeg-master-latest-win64-gpl/) срезается → binPath без версии.
                ["ffmpeg"] = new ToolConfig
                {
                    DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
                    ExtractTo = SelfBuildPaths.ExternalDirName + "/ffmpeg",
                    BinPath = SelfBuildPaths.ExternalDirName + "/ffmpeg/bin/ffmpeg.exe",
                    VersionArgs = "-version"
                },
                // poppler: имя ассета содержит версию (Release-26.09.0-0.zip), поэтому URL
                // зашпинивать нельзя — иначе «Проверить обновления» навсегда увидит одну и ту же
                // сборку. {tag}/{version} резолвятся в последний релиз на момент установки.
                // Каталог poppler-26.09.0/ срезается → binPath без версии.
                ["poppler"] = new ToolConfig
                {
                    DownloadUrl = "https://github.com/oschwartz10612/poppler-windows/releases/download/{tag}/Release-{version}.zip",
                    ExtractTo = SelfBuildPaths.ExternalDirName + "/poppler",
                    BinPath = SelfBuildPaths.ExternalDirName + "/poppler/Library/bin/pdftotext.exe",
                    // poppler-утилиты печатают версию по -v и в stderr, -version не понимают.
                    VersionArgs = "-v"
                }
            }
        };
        config.Save();
        return config;
    }
}

/// <summary>
/// Конфигурация одного инструмента (ffmpeg, poppler и т.д.).
///
/// Раскладка версионно-независимая: <see cref="StripTopLevelDir"/> срезает каталог
/// верхнего уровня из архива, поэтому BinPath не содержит номера версии и переживает
/// релиз новой сборки. README инструмента лежит рядом с бинарём и попадает в системный
/// промпт агента (Core.Agent.ExternalToolsNote) — отсюда и имя файла в конфиге.
/// </summary>
public sealed class ToolConfig
{
    /// <summary>Текущая версия (для отображения; фактическая читается запуском бинаря).</summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// URL для скачивания (zip-архив). Допускает подстановки {tag} и {version} —
    /// тег последнего релиза и он же без ведущей «v» ({tag}: v26.09.0-0,
    /// {version}: 26.09.0-0). Нет подстановок — URL фиксирован.
    /// </summary>
    public string DownloadUrl { get; set; } = "";

    /// <summary>Каталог для экстракции (относительно корня воркспейса).</summary>
    public string ExtractTo { get; set; } = "";

    /// <summary>Путь к бинарнику (относительно корня воркспейса, без каталога сборки).</summary>
    public string BinPath { get; set; } = "";

    /// <summary>
    /// Срезать единственный каталог верхнего уровня из архива. Нужно обоим текущим
    /// источникам: BtbN/FFmpeg-Builds (ffmpeg-master-latest-win64-gpl/) и
    /// oschwartz10612/poppler-windows (poppler-26.09.0/) заворачивают всё в каталог сборки.
    /// </summary>
    public bool StripTopLevelDir { get; set; } = true;

    /// <summary>Аргумент для запуска <c>bin --version</c>. ffmpeg понимает -version, poppler — -v.</summary>
    public string VersionArgs { get; set; } = "-version";

    /// <summary>Имя README инструмента: попадает в системный промпт агента.</summary>
    public string DocsFileName { get; set; } = SelfBuildPaths.ExternalDocsFileName;

    /// <summary>Абсолютный путь к бинарнику (workspaceRoot — корнем, иначе autodetect).</summary>
    public string BinPathFor(string? workspaceRoot = null) =>
        Path.Combine(workspaceRoot ?? SelfBuildPaths.WorkspaceRoot,
            BinPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Проверить, установлен ли инструмент.</summary>
    public bool IsInstalled(string? workspaceRoot = null) => File.Exists(BinPathFor(workspaceRoot));
}
