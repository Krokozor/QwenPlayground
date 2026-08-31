using System.Text.Json;
using QwenPlayground.Core.MetaInfo;

namespace QwenPlayground.Core.Tests;

public sealed class StateBlockTests
{
    private static StateBlock Sample() => new()
    {
        MsgId = 2,
        Time = new DateTime(2026, 8, 17, 13, 15, 26),
        ContextUsed = 12345,
        ContextMax = 32768,
        BuildId = "20260817-102607",
        BuildStatus = "success",
        Memories =
        {
            new StateBlock.MemoryRef { Id = "mem1", Relevance = 0.95, Content = "fact one" },
            new StateBlock.MemoryRef { Id = "mem2", Relevance = 0.42, Content = "fact two" }
        },
        MemoryNag = "do memory management",
        Nag = "call sanity_check"
    };

    [Fact]
    public void ToString_RendersCanonicalFormat()
    {
        var expected =
            "<state>\n" +
            "msg_id=2\n" +
            "time=2026-08-17 13:15:26\n" +
            "context=12345/32768\n" +
            "build=20260817-102607:success\n" +
            "mem=mem1 | relevance ~0.95 | fact one\n" +
            "mem=mem2 | relevance ~0.42 | fact two\n" +
            "mem_nag=do memory management\n" +
            "nag=call sanity_check\n" +
            "</state>";

        Assert.Equal(expected, Sample().ToString());
    }

    [Fact]
    public void ToString_SkipsEmptyFields()
    {
        var state = new StateBlock { MsgId = 5 };

        Assert.Equal("<state>\nmsg_id=5\n</state>", state.ToString());
    }

    [Fact]
    public void ToString_EmptyState_StillHasTags()
    {
        Assert.Equal("<state>\n</state>", new StateBlock().ToString());
    }

    [Fact]
    public void Parse_RoundTripsSimilarPairs()
    {
        var original = new StateBlock
        {
            MsgId = 9,
            SimilarPairs =
            {
                new StateBlock.MemoryPair("mem_aaa", "mem_bbb"),
                new StateBlock.MemoryPair("mem_ccc", "mem_ddd")
            }
        };

        var parsed = StateBlock.Parse(original.ToString());

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.SimilarPairs.Count);
        Assert.Equal("mem_aaa", parsed.SimilarPairs[0].A);
        Assert.Equal("mem_bbb", parsed.SimilarPairs[0].B);
        Assert.Equal("mem_ccc", parsed.SimilarPairs[1].A);
        // msg_id тоже сохранился: блок целиком round-trip
        Assert.Equal(9, parsed.MsgId);
    }

    [Fact]
    public void Parse_BrokenPairLine_DoesNotPoisonBlock()
    {
        var block = "<state>\nmsg_id=1\npair=garbage-without-tilde\n</state>";

        var parsed = StateBlock.Parse(block);

        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!.MsgId);
        Assert.Empty(parsed.SimilarPairs);
    }

    [Fact]
    public void Parse_RoundTripsFullBlock()
    {
        var rendered = Sample().ToString();

        var parsed = StateBlock.Parse(rendered);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.MsgId);
        Assert.Equal(new DateTime(2026, 8, 17, 13, 15, 26), parsed.Time);
        Assert.Equal(12345, parsed.ContextUsed);
        Assert.Equal(32768, parsed.ContextMax);
        Assert.Equal("20260817-102607", parsed.BuildId);
        Assert.Equal("success", parsed.BuildStatus);
        Assert.Equal(2, parsed.Memories.Count);
        Assert.Equal("mem1", parsed.Memories[0].Id);
        Assert.Equal(0.95, parsed.Memories[0].Relevance);
        Assert.Equal("fact one", parsed.Memories[0].Content);
        Assert.Equal("do memory management", parsed.MemoryNag);
        Assert.Equal("call sanity_check", parsed.Nag);
        Assert.Equal(rendered, parsed.ToString());
    }

    [Fact]
    public void Parse_ReturnsNull_WhenNotABlock()
    {
        Assert.Null(StateBlock.Parse("just text"));
        Assert.Null(StateBlock.Parse("<state>without close"));
        Assert.Null(StateBlock.Parse(null));
        Assert.Null(StateBlock.Parse(""));
    }

    [Fact]
    public void WithNag_MutatesExistingBlock()
    {
        var state = Sample();

        var result = StateBlock.WithNag(state, "nag text");

        Assert.Same(state, result);
        Assert.Equal("nag text", state.Nag);
        Assert.Equal("nag text", result.Nag);
    }

    [Fact]
    public void WithNag_CreatesBlockWhenNull()
    {
        var result = StateBlock.WithNag(null, "nag text");

        Assert.NotNull(result);
        Assert.Equal("nag text", result.Nag);
    }

    [Fact]
    public void Json_RoundTripsObject()
    {
        var json = JsonSerializer.Serialize(Sample());
        var back = JsonSerializer.Deserialize<StateBlock>(json);

        Assert.NotNull(back);
        Assert.Equal(2, back.MsgId);
        Assert.Equal(new DateTime(2026, 8, 17, 13, 15, 26), back.Time);
        Assert.Equal(12345, back.ContextUsed);
        Assert.Equal(32768, back.ContextMax);
        Assert.Equal("20260817-102607", back.BuildId);
        Assert.Equal("success", back.BuildStatus);
        Assert.Equal(2, back.Memories.Count);
        Assert.Equal("mem1", back.Memories[0].Id);
        Assert.Equal(0.95, back.Memories[0].Relevance);
        Assert.Equal("fact one", back.Memories[0].Content);
        Assert.Equal("do memory management", back.MemoryNag);
        Assert.Equal("call sanity_check", back.Nag);
        Assert.Equal(Sample().ToString(), back.ToString());
    }

    [Fact]
    public void Json_RoundTripsEmptyStateBlock()
    {
        var json = JsonSerializer.Serialize(new StateBlock());
        var back = JsonSerializer.Deserialize<StateBlock>(json);

        Assert.NotNull(back);
        Assert.Null(back.MsgId);
        Assert.Null(back.Time);
        Assert.Empty(back.Memories);
    }

    [Fact]
    public void Json_DeserializesNull()
    {
        Assert.Null(JsonSerializer.Deserialize<StateBlock>("null"));
    }
}