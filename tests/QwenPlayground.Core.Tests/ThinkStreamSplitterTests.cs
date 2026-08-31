using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Tests;

public sealed class ThinkStreamSplitterTests
{
    [Fact]
    public void NoMarker_EverythingIsReasoning()
    {
        var splitter = new ThinkStreamSplitter();
        splitter.Append("still thinking about it...");
        splitter.Flush(); // конец потока: отложенный хвост разрешается как текст

        Assert.False(splitter.ThinkClosed);
        Assert.Equal("still thinking about it...", splitter.Reasoning);
        Assert.Equal(string.Empty, splitter.Content);
    }

    [Fact]
    public void NoFlush_PendingTailNotPublished()
    {
        // Без Flush последние ThinkClose.Length-1 символов остаются неразрешёнными —
        // это осознанная цена инкрементального скана.
        var splitter = new ThinkStreamSplitter();
        splitter.Append("still thinking about it...");

        Assert.Equal("still thinking abou", splitter.Reasoning);
    }

    [Fact]
    public void MarkerInSingleChunk_SplitsAndTrimsLeadingNewlines()
    {
        var splitter = new ThinkStreamSplitter();
        splitter.Append("abc</think>\n\nhello world");

        Assert.True(splitter.ThinkClosed);
        Assert.Equal("abc", splitter.Reasoning);
        Assert.Equal("hello world", splitter.Content);
    }

    [Fact]
    public void MarkerAcrossChunks_IsDetected()
    {
        var splitter = new ThinkStreamSplitter();
        splitter.Append("ab");
        splitter.Append("</th");
        splitter.Append("ink>\n\nx");

        Assert.True(splitter.ThinkClosed);
        Assert.Equal("ab", splitter.Reasoning);
        Assert.Equal("x", splitter.Content);
    }

    [Fact]
    public void ChunkShorterThanMarker_KeptPendingUntilDecided()
    {
        var splitter = new ThinkStreamSplitter();
        splitter.Append("</th");

        // Нельзя ещё знать, маркер это или просто текст: ничего не публикуем.
        Assert.False(splitter.ThinkClosed);
        Assert.Equal(string.Empty, splitter.Reasoning);

        splitter.Append("in");
        Assert.False(splitter.ThinkClosed);

        splitter.Append("k>done");
        Assert.True(splitter.ThinkClosed);
        Assert.Equal(string.Empty, splitter.Reasoning);
        Assert.Equal("done", splitter.Content);
    }

    [Fact]
    public void FalseAlarmNearMarker_TextGoesToReasoning()
    {
        // "</thin" без продолжения-маркера — обычный текст мысли.
        var splitter = new ThinkStreamSplitter();
        splitter.Append("a</thin");
        splitter.Append("g b</think>c");

        Assert.True(splitter.ThinkClosed);
        Assert.Equal("a</thin" + "g b", splitter.Reasoning);
        Assert.Equal("c", splitter.Content);
    }

    [Fact]
    public void Prefill_WithMarker_SplitsImmediately()
    {
        var splitter = new ThinkStreamSplitter();
        splitter.AppendPrefill("old thought</think>\nold answer");
        splitter.Append(" more");

        Assert.True(splitter.ThinkClosed);
        Assert.Equal("old thought", splitter.Reasoning);
        Assert.Equal("old answer more", splitter.Content);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var splitter = new ThinkStreamSplitter();
        splitter.Append("a</think>b");
        splitter.Reset();

        Assert.False(splitter.ThinkClosed);
        Assert.Equal(string.Empty, splitter.Reasoning);
        Assert.Equal(string.Empty, splitter.Content);
    }

    [Fact]
    public void SecondMarkerInContent_StaysLiteralText_LikeFullParser()
    {
        var raw = "first</think>answer with </think> inside";

        var splitter = new ThinkStreamSplitter();
        splitter.Append(raw);
        var parsed = QwenOutputParser.ParseAssistant(raw);

        Assert.True(splitter.ThinkClosed);
        Assert.Equal(parsed.Reasoning, splitter.Reasoning);
        Assert.Equal(parsed.Content.Trim(), splitter.Content.Trim());
    }

    [Fact]
    public void CharByChar_MatchesFullParse()
    {
        const string raw = "  deep thought\nmulti line  </think>\n\nAnswer text here.";

        var splitter = new ThinkStreamSplitter();
        foreach (var ch in raw)
        {
            splitter.Append(ch.ToString());
        }

        var parsed = QwenOutputParser.ParseAssistant(raw);
        Assert.Equal(parsed.Reasoning, splitter.Reasoning);
        Assert.Equal(parsed.Content.Trim(), splitter.Content.Trim());
    }
}
