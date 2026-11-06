using System.Text.Json.Nodes;
using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Loop-guard (2026-11-05): хеш распарсенного тул-вызова — основа дедупликации
/// в пределах хода (AgentLoop). Повторный вызов с тем же именем и аргументами
/// должен давать тот же хеш — независимо от порядка ключей в JSON.
/// </summary>
public class LoopGuardTests
{
    [Fact]
    public void Hash_SameCall_SameHash()
    {
        var a = new JsonObject { ["path"] = "x.cs", ["old_string"] = "1" };
        var b = new JsonObject { ["path"] = "x.cs", ["old_string"] = "1" };
        Assert.Equal(LoopGuard.Hash("edit_file", a), LoopGuard.Hash("edit_file", b));
    }

    [Fact]
    public void Hash_DifferentArgs_DifferentHash()
    {
        var a = new JsonObject { ["path"] = "x.cs" };
        var b = new JsonObject { ["path"] = "y.cs" };
        Assert.NotEqual(LoopGuard.Hash("edit_file", a), LoopGuard.Hash("edit_file", b));
    }

    [Fact]
    public void Hash_DifferentName_DifferentHash()
    {
        var args = new JsonObject { ["path"] = "x.cs" };
        Assert.NotEqual(LoopGuard.Hash("read_file", args), LoopGuard.Hash("edit_file", args));
    }

    [Fact]
    public void Hash_KeyOrderDoesNotMatter()
    {
        var a = new JsonObject { ["b"] = "2", ["a"] = "1" };
        var b = new JsonObject { ["a"] = "1", ["b"] = "2" };
        Assert.Equal(LoopGuard.Hash("t", a), LoopGuard.Hash("t", b));
    }

    [Fact]
    public void Hash_EmptyAndNullArgs_Equal()
    {
        Assert.Equal(LoopGuard.Hash("t", new JsonObject()), LoopGuard.Hash("t", null));
        Assert.Equal(LoopGuard.Hash("t", null), LoopGuard.Hash("t", (JsonObject?)null));
    }

    private static ToolCall Call(string name, string key, string value) =>
        new() { Name = name, Arguments = new JsonObject { [key] = value } };

    [Fact]
    public void RemoveDuplicates_IntraMessage_KeepsFirstOccurrence()
    {
        var a = Call("read_file", "path", "x");
        var b = Call("read_file", "path", "y");
        var a2 = Call("read_file", "path", "x");
        var seen = new HashSet<string>();
        var result = LoopGuard.RemoveDuplicates(new[] { a, b, a2 }, seen);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Same(a, result[0]);
        Assert.Same(b, result[1]);
    }

    [Fact]
    public void RemoveDuplicates_AcrossTurn_SecondMessageCut()
    {
        var a = Call("read_file", "path", "x");
        var seen = new HashSet<string>();
        LoopGuard.RemoveDuplicates(new[] { a }, seen);
        // «Другое сообщение» того же хода с идентичным вызовом — вырезается.
        var a2 = Call("read_file", "path", "x");
        Assert.Null(LoopGuard.RemoveDuplicates(new[] { a2 }, seen));
    }

    [Fact]
    public void RemoveDuplicates_NoDuplicates_ReturnsSameReference()
    {
        var list = new[] { Call("t1", "a", "1"), Call("t2", "b", "2") };
        var seen = new HashSet<string>();
        Assert.Same(list, LoopGuard.RemoveDuplicates(list, seen));
    }

    [Fact]
    public void RemoveDuplicates_AllDuplicates_ReturnsNull()
    {
        var seen = new HashSet<string> { LoopGuard.Hash("t", new JsonObject { ["x"] = "1" }) };
        Assert.Null(LoopGuard.RemoveDuplicates(new[] { Call("t", "x", "1") }, seen));
    }
}
