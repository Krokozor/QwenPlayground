using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.MetaInfo;

namespace QwenPlayground.Core.Tests;

public sealed class ContextCompactorTests
{
    [Fact]
    public void Boundary_KeepsTailAroundKeepRatio()
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 10; i++)
        {
            messages.Add(ChatMessage.User(new string('u', 400)));
            messages.Add(ChatMessage.Assistant(new string('a', 400)));
        }

        var boundary = ContextCompactor.FindCompactionBoundary(messages, 0.5);

        Assert.InRange(boundary, 8, 14);
        Assert.Equal(ChatRole.User, messages[boundary].Role);
    }

    [Fact]
    public void Boundary_WindowSize_CapsKeepBudget()
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 20; i++)
        {
            messages.Add(ChatMessage.User(new string('u', 400)));
            messages.Add(ChatMessage.Assistant(new string('a', 400)));
        }

        var withoutWindow = ContextCompactor.FindCompactionBoundary(messages, 0.9);
        var withWindow = ContextCompactor.FindCompactionBoundary(messages, 0.9, windowSize: 1000);

        // Маленькое окно не даёт удержать 90%: с окном хвост короче.
        Assert.True(withWindow > withoutWindow, $"withWindow={withWindow} должно быть больше withoutWindow={withoutWindow}");
        Assert.Equal(ChatRole.User, messages[withWindow].Role);
    }

    [Fact]
    public void Boundary_DoesNotSplitToolChain()
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 8; i++)
        {
            messages.Add(ChatMessage.User(new string('u', 400)));
            messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = new string('a', 200),
                ToolCalls = new List<ToolCall> { new() { Name = "read_file", Arguments = JsonNode.Parse("""{"path":"a"}""")! } }
            });
            messages.Add(ChatMessage.Tool(new string('t', 400)));
        }

        var boundary = ContextCompactor.FindCompactionBoundary(messages, 0.5);

        Assert.Equal(ChatRole.User, messages[boundary].Role);
        Assert.True(boundary > 0);
    }

    [Fact]
    public void Boundary_TooShortConversation_ReturnsZero()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            ChatMessage.User("hi"),
            ChatMessage.Assistant("hey")
        };

        Assert.Equal(0, ContextCompactor.FindCompactionBoundary(messages, 0.5));
    }

    [Fact]
    public void BuildTranscript_IncludesStateBlockForChronology()
    {
        // State-блок (time, context, build, mem) должен попасть в транскрипт: суммаризатору
        // нужна хронология сегмента, а не голый текст.
        var message = new ChatMessage { Id = 5, Role = ChatRole.Assistant, Content = "привет" };
        message.StateBlock = new StateBlock { MsgId = 5, Time = new DateTime(2026, 9, 4, 8, 30, 0) };

        var transcript = ContextCompactor.BuildTranscript(new[] { message }, 1);

        Assert.Contains("### assistant", transcript);
        Assert.Contains("<state>", transcript);
        Assert.Contains("time=2026-09-04 08:30:00", transcript);
        Assert.True(transcript.IndexOf("<state>", StringComparison.Ordinal) <
                    transcript.IndexOf("привет", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildTranscript_WithoutStateBlock_StaysClean()
    {
        var transcript = ContextCompactor.BuildTranscript(
            new[] { ChatMessage.Assistant("привет") }, 1);

        Assert.DoesNotContain("<state>", transcript);
        Assert.Contains("### assistant", transcript);
    }
}
