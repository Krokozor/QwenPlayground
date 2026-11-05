using System.Text.Json.Nodes;
using QwenPlayground.Core.Agent;

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
}
