using System.IO;
using QwenPlayground.Core.Agent;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Секция «внешние инструменты» в системном промпте: собирается из README каждого
/// подкаталога external/. Инструменты опциональны, поэтому контракт — «каталог с README
/// даёт секцию, без каталога секции нет», а не «секция всегда про ffmpeg и poppler».
/// </summary>
public sealed class ExternalToolsNoteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qpw_extnote_" + Guid.NewGuid().ToString("N"));

    private void WriteTool(string tool, string readme)
    {
        var dir = Path.Combine(_dir, tool);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "README.md"), readme);
    }

    private void WriteToolDirOnly(string tool)
    {
        Directory.CreateDirectory(Path.Combine(_dir, tool));
    }

    [Fact]
    public void NoExternalDir_NoSection()
    {
        var missing = Path.Combine(_dir, "does-not-exist");
        Assert.Null(new ExternalToolsNote(missing).Get());
    }

    [Fact]
    public void EmptyExternalDir_NoSection()
    {
        Directory.CreateDirectory(_dir);
        Assert.Null(new ExternalToolsNote(_dir).Get());
    }

    [Fact]
    public void ToolWithoutReadme_ContributesNothing()
    {
        WriteToolDirOnly("no-docs");
        Assert.Null(new ExternalToolsNote(_dir).Get());
    }

    [Fact]
    public void OneTool_YieldsSectionWithToolName()
    {
        WriteTool("ffmpeg", "# ffmpeg\n\n- `external/ffmpeg/bin/ffmpeg.exe`\n");

        var section = new ExternalToolsNote(_dir).Get();

        Assert.NotNull(section);
        Assert.Contains("ffmpeg", section);
        Assert.Contains("external/ffmpeg/bin/ffmpeg.exe", section);
    }

    [Fact]
    public void TwoTools_AreConcatenatedInNameOrder()
    {
        WriteTool("poppler", "# poppler\n\npdftotext\n");
        WriteTool("ffmpeg", "# ffmpeg\n\nffmpeg.exe\n");

        var section = new ExternalToolsNote(_dir).Get();

        Assert.NotNull(section);
        Assert.True(
            section.IndexOf("ffmpeg", StringComparison.Ordinal) < section.IndexOf("poppler", StringComparison.Ordinal),
            "секции должны идти по имени каталога — порядок стабилен (cache-anchor)");
        Assert.Contains("pdftotext", section);
    }

    [Fact]
    public void SectionHasExactlyOneH1_AndH2PerTool()
    {
        WriteTool("ffmpeg", "# ffmpeg\n\nbody\n");
        WriteTool("poppler", "# poppler\n\nbody\n");

        var section = new ExternalToolsNote(_dir).Get();

        Assert.NotNull(section);
        var h1 = section.Split('\n').Count(l => l.StartsWith("# ", StringComparison.Ordinal));
        var h2 = section.Split('\n').Count(l => l.StartsWith("## ", StringComparison.Ordinal));
        Assert.Equal(1, h1);
        Assert.Equal(2, h2);
    }

    [Fact]
    public void ForeignH1_IsKeptAsIs()
    {
        // H1 не совпадает с именем каталога — значит чужой/осмысленный заголовок,
        // не выкидываем: секция не должна терять содержимое из-за имени папки.
        WriteTool("mytool", "# Not the tool name\n\nbody\n");

        var section = new ExternalToolsNote(_dir).Get();

        Assert.NotNull(section);
        Assert.Contains("# Not the tool name", section);
    }

    [Fact]
    public void InnerHeadings_NestUnderToolHeading()
    {
        // Без сдвига «## Approach» внутри ffmpeg был бы соседом «## ffmpeg», а не подразделом.
        WriteTool("ffmpeg", "# ffmpeg\n\n## Approach\n\nbody\n");

        var section = new ExternalToolsNote(_dir).Get();

        Assert.NotNull(section);
        Assert.Contains("## ffmpeg", section);
        Assert.Contains("### Approach", section);
    }

    [Fact]
    public void HashInsideCodeFence_IsNotAHeading()
    {
        WriteTool("ffmpeg", "# ffmpeg\n\n```sh\n# not a heading\ngrep -r x .\n```\n\n## Approach\n");

        var section = new ExternalToolsNote(_dir).Get();

        Assert.NotNull(section);
        Assert.Contains("# not a heading", section);
        Assert.Contains("### Approach", section);
    }

    [Fact]
    public void EmptyReadme_ContributesNothing()
    {
        WriteTool("blank", "   \n\n");
        WriteTool("ffmpeg", "# ffmpeg\n\nbody\n");

        var section = new ExternalToolsNote(_dir).Get();

        Assert.NotNull(section);
        Assert.DoesNotContain("blank", section);
    }

    [Fact]
    public void NewToolFolder_AppearsWithoutRecreatingNote()
    {
        WriteTool("ffmpeg", "# ffmpeg\n\nbody\n");
        var note = new ExternalToolsNote(_dir);
        Assert.DoesNotContain("pdftotext", note.Get() ?? "");

        // Кэш инвалидируется по СОСТАВУ зависимостей: новый каталог с README, тот же инстанс.
        WriteTool("poppler", "# poppler\n\npdftotext\n");

        Assert.Contains("pdftotext", note.Get() ?? "");
    }

    [Fact]
    public void EditedReadme_RefreshedWithoutRecreatingNote()
    {
        WriteTool("ffmpeg", "# ffmpeg\n\nv1\n");
        var note = new ExternalToolsNote(_dir);
        Assert.Contains("v1", note.Get() ?? "");

        File.WriteAllText(Path.Combine(_dir, "ffmpeg", "README.md"), "# ffmpeg\n\nv2\n");
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "ffmpeg", "README.md"), DateTime.UtcNow.AddSeconds(2));

        Assert.Contains("v2", note.Get() ?? "");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
