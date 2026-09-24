using QwenPlayground.Core.Tools.Builtins;

namespace QwenPlayground.Core.Tests;

/// <summary>Чистые тесты IntuitionFormat (оформление вывода тулов «интуиции»).</summary>
public sealed class IntuitionFormatTests
{
    private static readonly (string Name, double Value)[] Items =
    [
        ("alpha", 0.7),
        ("beta", 0.3),
    ];

    [Fact]
    public void Compact_SingleLineWithCommas()
    {
        var result = IntuitionFormat.Distribution(IntuitionFormat.Compact, "Distribution", Items);

        Assert.StartsWith("Distribution: ", result);
        Assert.DoesNotContain('\n', result);
        Assert.Contains(", ", result);
        Assert.Contains("alpha=", result);
        Assert.Contains("beta=", result);
    }

    [Fact]
    public void Lines_OneItemPerLineNoBars()
    {
        var result = IntuitionFormat.Distribution(IntuitionFormat.Lines, "Distribution", Items);

        Assert.StartsWith("Distribution:\n", result);
        var lines = result.Split('\n');
        Assert.Equal(3, lines.Length); // header + 2 items
        Assert.StartsWith("alpha=", lines[1]);
        Assert.StartsWith("beta=", lines[2]);
        Assert.DoesNotContain('=', result.Replace("Distribution:", string.Empty).Replace("alpha=", string.Empty).Replace("beta=", string.Empty));
    }

    [Fact]
    public void LinesWithBars_AppendsTenCellBar()
    {
        var result = IntuitionFormat.Distribution(IntuitionFormat.LinesWithBars, "Distribution", Items);

        var lines = result.Split('\n');
        Assert.Equal(3, lines.Length);
        // Бар относительно максимума: alpha=0.7 → полный, beta=0.3 → ~4 ячейки (0.3/0.7≈0.43).
        Assert.EndsWith("==========", lines[1]);
        Assert.EndsWith("====------", lines[2]);
    }

    [Fact]
    public void UnknownMode_FallsBackToLines()
    {
        var result = IntuitionFormat.Distribution("whatever", "Distribution", Items);

        Assert.StartsWith("Distribution:\n", result);
        Assert.DoesNotContain("====", result);
    }

    [Fact]
    public void EmptyItems_ReturnsHeaderOnly()
    {
        var result = IntuitionFormat.Distribution(IntuitionFormat.Lines, "Distribution",
            Array.Empty<(string, double)>());

        Assert.Equal("Distribution: ", result);
    }

    [Theory]
    [InlineData(0.0, "----------")]
    [InlineData(1.0, "==========")]
    [InlineData(0.3, "===-------")]
    [InlineData(0.7, "=======---")]
    [InlineData(-0.5, "----------")] // clamp
    [InlineData(1.5, "==========")] // clamp
    public void Bar_TenCellsClamped(double value, string expected)
    {
        Assert.Equal(expected, IntuitionFormat.Bar(value));
    }
}
