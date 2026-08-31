using System.IO;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Tools;
using QwenPlayground.Core.Tools.Builtins;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Капы размера вывода инструментов: grep/read_file/memory_list не должны возвращать
/// простыню, съедающую контекст модели (см. refactoring.md, changelog 2026-08-22).
/// </summary>
public sealed class ToolOutputCapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qpw_captest_" + Guid.NewGuid().ToString("N"));

    private ToolContext Context() => new(_dir);

    [Fact]
    public async Task Grep_TruncatesAtMatchLimit()
    {
        Directory.CreateDirectory(_dir);
        var lines = Enumerable.Range(1, 150).Select(i => $"needle {i}").ToArray();
        File.WriteAllLines(Path.Combine(_dir, "many.txt"), lines);

        var result = await new GrepTool { Pattern = "needle" }.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Contains("truncated", result);
        Assert.True(result.Length < 8000 + 500, "вывод не должен заметно превышать кап");
        Assert.DoesNotContain("needle 101\n", result); // после лимита матчи не добавляются
    }

    [Fact]
    public async Task Grep_TrimsLongLines()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "long.txt"), "hit " + new string('x', 2000));

        var result = await new GrepTool { Pattern = "hit" }.ExecuteAsync(Context(), CancellationToken.None);

        Assert.True(result.Length < 1000, $"строка матча должна обрезаться, длина={result.Length}");
        Assert.EndsWith("…", result.Trim());
    }

    [Fact]
    public async Task Grep_NoMatches()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "hello world");

        var result = await new GrepTool { Pattern = "zzz" }.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal("no matches", result);
    }

    [Fact]
    public async Task ReadFile_TruncatesAtTotalCap_AndSuggestsOffset()
    {
        Directory.CreateDirectory(_dir);
        // Каждая строка ~400 символов: кап 32000 символов достигается до Limit=400 строк.
        var lines = Enumerable.Range(1, 400).Select(_ => new string('a', 400)).ToArray();
        File.WriteAllLines(Path.Combine(_dir, "big.txt"), lines);

        var result = await new ReadFileTool { Path = "big.txt" }.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Contains("output cap reached", result);
        Assert.Contains("offset", result);
        Assert.True(result.Length < 32000 + 500, $"вывод не должен заметно превышать кап, длина={result.Length}");
    }

    [Fact]
    public async Task ReadFile_TrimsLongLine_WithTailMarker()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllLines(Path.Combine(_dir, "one.txt"), [new string('y', 3000)]);

        var result = await new ReadFileTool { Path = "one.txt" }.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Contains("[+", result);
        Assert.Contains(" chars]", result);
        Assert.True(result.Length < 1000, $"длинная строка должна обрезаться, длина={result.Length}");
    }

    [Fact]
    public async Task ReadFile_TooLargeFile_ReturnsErrorInsteadOfReading()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "huge.txt");
        using (var stream = File.Create(path))
        {
            stream.SetLength(17 * 1024 * 1024); // > 16 MB
        }

        var result = await new ReadFileTool { Path = "huge.txt" }.ExecuteAsync(Context(), CancellationToken.None);

        Assert.StartsWith("Error: file too large", result);
    }

    [Fact]
    public async Task MemoryList_TruncatesWithRemainingCount()
    {
        var store = new MemoryStore(_dir);
        for (var i = 0; i < 205; i++)
        {
            store.Add($"факт номер {i}");
        }

        var result = await new MemoryListTool(_dir).ExecuteAsync(Context(), CancellationToken.None);

        Assert.Contains("Total memories: 205", result);
        Assert.Contains("5 more", result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
