using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Tools;
using Xunit;
using MainFacade = QwenPlayground.Core.Main.Main;
using UiHooks = QwenPlayground.Core.Main.UiHooks;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// ChatRuntime (стадия B мультиоконного квеста): Main собирает бандл пер-разговорных
/// сервисов в Runtime; форварды фасада указывают на те же инстансы; SessionId следует
/// за текущей сессией (динамический рантайм главного окна).
/// </summary>
public sealed class ChatRuntimeTests {
    private static MainFacade BuildMain() => new(new UiHooks(
        _ => { },
        _ => { },
        () => string.Empty,
        _ => { },
        () => { },
        _ => Task.CompletedTask,
        () => Task.CompletedTask,
        () => { },
        () => { }),
        typeof(AgentTool).Assembly);

    [Fact]
    public void Main_BundlesPerConversationServices_InRuntime() {
        var main = BuildMain();

        Assert.Same(main.Log, main.Runtime.Log);
        Assert.Same(main.ChatState, main.Runtime.ChatState);
        Assert.Same(main.Compaction, main.Runtime.Compaction);
        Assert.Same(main.MemorySurfacer, main.Runtime.MemorySurfacer);
        Assert.Same(main.PromptAssembler, main.Runtime.PromptAssembler);
        Assert.Same(main.StateBlocks, main.Runtime.StateBlocks);
        Assert.Same(main.Pipeline, main.Runtime.Pipeline);
        Assert.Same(main.Maintenance, main.Runtime.Maintenance);
        Assert.Same(main.Draft, main.Runtime.Draft);
        Assert.Same(main.Turns, main.Runtime.Turns);
    }

    [Fact]
    public void Runtime_SessionId_FollowsCurrentSession() {
        var main = BuildMain();

        Assert.Equal(main.Sessions.CurrentId, main.Runtime.SessionId());
    }

    [Fact]
    public void Runtime_EffectiveContextSize_IsPositive() {
        var main = BuildMain();

        Assert.True(main.Runtime.EffectiveContextSize > 0);
    }
}
