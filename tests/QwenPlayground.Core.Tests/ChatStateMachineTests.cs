using QwenPlayground.Core.Chat;

namespace QwenPlayground.Core.Tests;

public sealed class ChatStateMachineTests
{
    [Fact]
    public void InitialState_IsIdle()
    {
        var fsm = new ChatStateMachine();

        Assert.Equal(ChatState.Idle, fsm.Current);
        Assert.False(fsm.IsBusy);
    }

    [Fact]
    public void Idle_To_Generating_IsAllowed()
    {
        var fsm = new ChatStateMachine();

        fsm.Transition(ChatState.Generating);

        Assert.Equal(ChatState.Generating, fsm.Current);
        Assert.True(fsm.IsBusy);
    }

    [Fact]
    public void Generating_To_Compacting_IsAllowed()
    {
        var fsm = new ChatStateMachine();
        fsm.Transition(ChatState.Generating);

        fsm.Transition(ChatState.Compacting);

        Assert.Equal(ChatState.Compacting, fsm.Current);
    }

    [Fact]
    public void Compacting_To_Generating_IsAllowed()
    {
        var fsm = new ChatStateMachine();
        fsm.Transition(ChatState.Generating);
        fsm.Transition(ChatState.Compacting);

        fsm.Transition(ChatState.Generating);

        Assert.Equal(ChatState.Generating, fsm.Current);
    }

    [Fact]
    public void Compacting_To_Idle_IsAllowed()
    {
        var fsm = new ChatStateMachine();
        fsm.Transition(ChatState.Compacting);

        fsm.Transition(ChatState.Idle);

        Assert.Equal(ChatState.Idle, fsm.Current);
    }

    [Fact]
    public void Generating_To_AwaitingUser_IsAllowed()
    {
        var fsm = new ChatStateMachine();
        fsm.Transition(ChatState.Generating);

        fsm.Transition(ChatState.AwaitingUser);

        Assert.Equal(ChatState.AwaitingUser, fsm.Current);
    }

    [Fact]
    public void AwaitingUser_To_Generating_IsAllowed()
    {
        var fsm = new ChatStateMachine();
        fsm.Transition(ChatState.Generating);
        fsm.Transition(ChatState.AwaitingUser);

        fsm.Transition(ChatState.Generating);

        Assert.Equal(ChatState.Generating, fsm.Current);
    }

    [Fact]
    public void Idle_To_Compacting_IsAllowed()
    {
        var fsm = new ChatStateMachine();

        fsm.Transition(ChatState.Compacting);

        Assert.Equal(ChatState.Compacting, fsm.Current);
    }

    [Fact]
    public void InvalidTransition_Throws()
    {
        var fsm = new ChatStateMachine();

        // Idle → AwaitingUser не разрешено
        Assert.Throws<InvalidOperationException>(() => fsm.Transition(ChatState.AwaitingUser));
    }

    [Fact]
    public void TryTransition_ReturnsFalse_OnInvalid()
    {
        var fsm = new ChatStateMachine();

        var result = fsm.TryTransition(ChatState.AwaitingUser);

        Assert.False(result);
        Assert.Equal(ChatState.Idle, fsm.Current);
    }

    [Fact]
    public void StateChanged_Event_Fires()
    {
        var fsm = new ChatStateMachine();
        var transitions = new List<(ChatState From, ChatState To)>();
        fsm.StateChanged += (from, to) => transitions.Add((from, to));

        fsm.Transition(ChatState.Generating);
        fsm.Transition(ChatState.Idle);

        Assert.Equal(2, transitions.Count);
        Assert.Equal((ChatState.Idle, ChatState.Generating), transitions[0]);
        Assert.Equal((ChatState.Generating, ChatState.Idle), transitions[1]);
    }

    [Fact]
    public void RestartPending_IsTerminal()
    {
        var fsm = new ChatStateMachine();
        fsm.Transition(ChatState.RestartPending);

        // Из RestartPending нельзя никуда
        Assert.Throws<InvalidOperationException>(() => fsm.Transition(ChatState.Idle));
        Assert.False(fsm.TryTransition(ChatState.Generating));
    }
}
