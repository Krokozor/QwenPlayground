using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>URL git-репозитория.</summary>
    public string Repo { get; set; } = "https://github.com/Krokozor/QwenPlayground.git";

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
            Repo = "https://github.com/Krokozor/QwenPlayground.git",
            Branch = "main",
            Tools = new Dictionary<string, ToolConfig>
            {
                ["ffmpeg"] = new ToolConfig
                {
                    Version = "7.1",
                    DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
                    ExtractTo = SelfBuildPaths.ExternalDirName + "/ffmpeg",
                    BinPath = SelfBuildPaths.ExternalDirName + "/ffmpeg/bin/ffmpeg.exe"
                }
            }
        };
        config.Save();
        return config;
    }
}

/// <summary>
/// Конфигурация одного инструмента (ffmpeg, ffprobe и т.д.).
/// </summary>
public sealed class ToolConfig
{
    /// <summary>Текущая версия (для отображения и сравнения).</summary>
    public string Version { get; set; } = "";

    /// <summary>URL для скачивания (zip-архив).</summary>
    public string DownloadUrl { get; set; } = "";

    /// <summary>Каталог для экстракции (относительно корня воркспейса).</summary>
    public string ExtractTo { get; set; } = "";

    /// <summary>Путь к бинарнику (относительно корня воркспейса).</summary>
    public string BinPath { get; set; } = "";

    /// <summary>Проверить, установлен ли инструмент.</summary>
    public bool IsInstalled()
    {
        var binPath = Path.Combine(SelfBuildPaths.WorkspaceRoot, BinPath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(binPath);
    }
}
