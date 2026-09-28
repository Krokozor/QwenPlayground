using System.IO;
using System.IO.Compression;
using QwenPlayground.Launcher;

namespace QwenPlayground.Launcher.Tests;

/// <summary>
/// Раскладка после экстракции — главный регресс инструментальных кнопок лаунчера.
/// Релизные zip заворачивают всё в каталог сборки (ffmpeg-master-latest-win64-gpl/,
/// poppler-26.09.0/); без срезания BinPath ловил бы «не найден после экстракции»,
/// а BinPath с номером версии ломался бы на каждом релизе.
/// </summary>
public sealed class ToolManagerExtractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qpw_toolextract_" + Guid.NewGuid().ToString("N"));

    /// <summary>Собрать zip, положив файлы по указанным путям внутри архива.</summary>
    private string MakeZip(params (string EntryPath, string Content)[] entries)
    {
        Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var fs = File.Create(zip))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (entryPath, content) in entries)
            {
                var entry = archive.CreateEntry(entryPath);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return zip;
    }

    private static ToolConfig Tool(bool strip) => new()
    {
        ExtractTo = "external/poppler",
        BinPath = "external/poppler/Library/bin/pdftotext.exe",
        StripTopLevelDir = strip
    };

    [Fact]
    public void VersionedRootDir_IsStripped_SoBinPathResolves()
    {
        var zip = MakeZip(
            ("poppler-26.09.0/Library/bin/pdftotext.exe", "stub"),
            ("poppler-26.09.0/README.md", "# poppler"));
        var tool = Tool(strip: true);
        var extractDir = Path.Combine(_root, tool.ExtractTo.Replace('/', Path.DirectorySeparatorChar));

        ToolManager.ExtractToolZip(tool, zip, extractDir);

        Assert.True(tool.IsInstalled(_root), "BinPath должен находиться после срезания каталога сборки");
        Assert.False(Directory.Exists(Path.Combine(extractDir, "poppler-26.09.0")));
        Assert.True(File.Exists(Path.Combine(extractDir, "README.md")), "содержимое каталога сборки поднимается целиком");
    }

    [Fact]
    public void FfmpegLayoutRootDir_IsStripped_SoBinPathResolves()
    {
        // Реальная раскладка BtbN/FFmpeg-Builds.
        var zip = MakeZip(
            ("ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe", "stub"),
            ("ffmpeg-master-latest-win64-gpl/bin/ffprobe.exe", "stub"),
            ("ffmpeg-master-latest-win64-gpl/presets/libvpx-1080p.ffpreset", "x"));
        var tool = new ToolConfig
        {
            ExtractTo = "external/ffmpeg",
            BinPath = "external/ffmpeg/bin/ffmpeg.exe",
            StripTopLevelDir = true
        };
        var extractDir = Path.Combine(_root, tool.ExtractTo.Replace('/', Path.DirectorySeparatorChar));

        ToolManager.ExtractToolZip(tool, zip, extractDir);

        Assert.True(tool.IsInstalled(_root));
        Assert.True(File.Exists(Path.Combine(extractDir, "bin", "ffprobe.exe")));
        Assert.True(File.Exists(Path.Combine(extractDir, "presets", "libvpx-1080p.ffpreset")));
    }

    [Fact]
    public void StripDisabled_KeepsRootDir()
    {
        var zip = MakeZip(("poppler-26.09.0/Library/bin/pdftotext.exe", "stub"));
        var tool = Tool(strip: false);
        var extractDir = Path.Combine(_root, tool.ExtractTo.Replace('/', Path.DirectorySeparatorChar));

        ToolManager.ExtractToolZip(tool, zip, extractDir);

        Assert.True(Directory.Exists(Path.Combine(extractDir, "poppler-26.09.0", "Library", "bin")));
    }

    [Fact]
    public void FlatArchive_IsLeftFlat()
    {
        var zip = MakeZip(("Library/bin/pdftotext.exe", "stub"), ("README.md", "# poppler"));
        var tool = Tool(strip: true);
        var extractDir = Path.Combine(_root, tool.ExtractTo.Replace('/', Path.DirectorySeparatorChar));

        ToolManager.ExtractToolZip(tool, zip, extractDir);

        Assert.True(tool.IsInstalled(_root));
        Assert.True(File.Exists(Path.Combine(extractDir, "README.md")));
    }

    [Fact]
    public void ArchiveWithTwoTopLevelEntries_IsLeftAlone()
    {
        // Срезать нечего неоднозначно — оставляем как есть, лучше лишняя папка,
        // чем содержимое, разложенное наугад.
        var zip = MakeZip(("a/bin/pdftotext.exe", "stub"), ("b/README.md", "x"));
        var tool = Tool(strip: true);
        var extractDir = Path.Combine(_root, tool.ExtractTo.Replace('/', Path.DirectorySeparatorChar));

        ToolManager.ExtractToolZip(tool, zip, extractDir);

        Assert.True(Directory.Exists(Path.Combine(extractDir, "a")));
        Assert.True(Directory.Exists(Path.Combine(extractDir, "b")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
