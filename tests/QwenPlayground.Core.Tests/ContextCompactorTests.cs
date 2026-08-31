using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;

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
    public void ApplyCompaction_KeepsSystemAndTail_InsertsSummary()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            ChatMessage.User("old1"),
            ChatMessage.Assistant("old2"),
            ChatMessage.User("recent1"),
            ChatMessage.Assistant("recent2")
        };

        var result = ContextCompactor.ApplyCompaction(messages, boundary: 3, summary: "SUMMARY");

        Assert.Equal(3, result.Count);
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.StartsWith("sys", result[0].Content);
        Assert.Contains("SUMMARY", result[0].Content);
        Assert.Equal("recent1", result[1].Content);
        Assert.Equal("recent2", result[2].Content);
    }

    [Fact]
    public void ApplyCompaction_ReplacesPreviousSummary()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("sys\n\n" + ContextCompactor.SummaryMarker + "\nOLD SUMMARY"),
            ChatMessage.User("old1"),
            ChatMessage.User("recent1")
        };

        var result = ContextCompactor.ApplyCompaction(messages, boundary: 2, summary: "NEW SUMMARY");

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain("OLD SUMMARY", result[0].Content);
        Assert.Contains("NEW SUMMARY", result[0].Content);
        Assert.StartsWith("sys", result[0].Content);
    }

    [Fact]
    public void SummarizationRequest_ContainsTranscriptAndInstruction()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            ChatMessage.User("сделай файл"),
            ChatMessage.Assistant("сделал", "надо сделать файл")
        };

        var (system, user) = ContextCompactor.BuildSummarizationRequest(messages, 3);

        Assert.Contains("summaries", system);
        Assert.Contains("сделай файл", user);
        Assert.Contains("надо сделать файл", user);
        Assert.Contains("summary", user);
    }

    [Fact]
    public void SummarizationRequest_HasNuancedStructure()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("решение"),
            ChatMessage.Assistant("ок")
        };

        var (_, user) = ContextCompactor.BuildSummarizationRequest(messages, 2);

        Assert.Contains("verbatim", user);
        Assert.Contains("Open threads", user);
        Assert.Contains("Current state", user);
    }

    [Fact]
    public void SummarizationRequest_NoTruncation_WhenSegmentFitsWindow()
    {
        var longContent = new string('x', 5000);
        var messages = new List<ChatMessage> { ChatMessage.User(longContent) };

        var (_, user) = ContextCompactor.BuildSummarizationRequest(messages, 1, windowSize: 204800);

        Assert.Contains(longContent, user);
        Assert.DoesNotContain("... (+", user);
    }

    [Fact]
    public void SummarizationRequest_Truncates_WhenSegmentExceedsWindow()
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 10; i++)
        {
            messages.Add(ChatMessage.User(new string('x', 100_000)));
        }

        var (_, user) = ContextCompactor.BuildSummarizationRequest(messages, 10, windowSize: 10_000);

        Assert.Contains("... (+", user);
    }
}
