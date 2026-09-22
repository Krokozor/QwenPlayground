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
        // Граница — ровно по бюджету, привязки к user-ролям нет (хвост может начинаться
        // с assistant); жёстко только «не с tool-сообщения».
        Assert.NotEqual(ChatRole.Tool, messages[boundary].Role);
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
        Assert.NotEqual(ChatRole.Tool, messages[withWindow].Role);
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

        Assert.True(boundary > 0);
        // Хвост не начинается с tool-сообщения (результат без вызова — битый рендер).
        Assert.NotEqual(ChatRole.Tool, messages[boundary].Role);
    }

    [Fact]
    public void Boundary_ParallelToolChain_NotSplit()
    {
        // assistant с ДВУМЯ вызовами + два tool-результата: бюджетное место может попасть
        // на второй tool — вся цепочка (с assistant'ом) уходит в хвост целиком.
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 3; i++)
        {
            messages.Add(ChatMessage.User(new string('u', 400)));
            messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = new string('a', 200),
                ToolCalls = new List<ToolCall>
                {
                    new() { Name = "read_file", Arguments = JsonNode.Parse("""{"path":"a"}""")! },
                    new() { Name = "read_file", Arguments = JsonNode.Parse("""{"path":"b"}""")! }
                }
            });
            messages.Add(ChatMessage.Tool(new string('t', 400)));
            messages.Add(ChatMessage.Tool(new string('t', 400)));
        }

        var boundary = ContextCompactor.FindCompactionBoundary(messages, 0.5);

        Assert.Equal(5, boundary);
        Assert.Equal(ChatRole.Assistant, messages[boundary].Role);
        Assert.Equal(2, messages[boundary].ToolCalls!.Count);
        Assert.Equal(ChatRole.Tool, messages[boundary + 1].Role);
        Assert.Equal(ChatRole.Tool, messages[boundary + 2].Role);
    }

    [Fact]
    public void Boundary_LongToolLoop_LandsInChain_KeepsChainIntact()
    {
        // Регрессия «нечего сжимать» при полном контексте: длинный тул-цикл — хвост
        // (по keepRatio) целиком из assistant/tool, user-запрос давно в голове.
        // Раньше граница уезжала за конец списка (0); теперь садится по бюджету,
        // и если место — в середине тул-цепочки, цепочка уходит в хвост целиком.
        var messages = new List<ChatMessage>
        {
            ChatMessage.User(new string('u', 400)),
            ChatMessage.User(new string('v', 400))
        };
        for (var i = 0; i < 20; i++)
        {
            messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = new string('a', 400),
                ToolCalls = new List<ToolCall> { new() { Name = "shell", Arguments = JsonNode.Parse("""{"command":"x"}""")! } }
            });
            messages.Add(ChatMessage.Tool(new string('t', 400)));
        }

        var boundary = ContextCompactor.FindCompactionBoundary(messages, 0.5);

        Assert.NotEqual(0, boundary);
        Assert.NotEqual(ChatRole.Tool, messages[boundary].Role);
        // Бюджетное место (21) — tool; цепочка отодвинулась на assistant с вызовом.
        Assert.Equal(20, boundary);
        Assert.Equal(ChatRole.Assistant, messages[boundary].Role);
        Assert.NotNull(messages[boundary].ToolCalls);
    }

    [Fact]
    public void Boundary_UsesExactMessageTokens_WhenProvided()
    {
        // Явные серверные веса определяют границу, а не длина текста: первые 5 сообщений
        // крошечные по символам, но тяжёлые по токенам (крупные tool-выводы в реальном
        // промпте). По chars/4 граница была бы ~5; по весам — 2.
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 10; i++)
        {
            messages.Add(ChatMessage.User("x"));
        }
        var tokens = new[] { 1000, 1000, 1000, 1000, 1000, 10, 10, 10, 10, 10 };

        var boundary = ContextCompactor.FindCompactionBoundary(messages, 0.5, messageTokens: tokens);

        Assert.Equal(2, boundary);
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
