using QwenPlayground.Core.Chat;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Tests;

public sealed class QwenOutputParserTests
{
    [Fact]
    public void Parse_ThinkingAndToolCall_ExtractsAllParts()
    {
        const string raw = """
            I should read it.
            </think>

            <tool_call>
            <function=read_file>
            <parameter=path>
            /tmp/a.txt
            </parameter>
            </function>
            </tool_call>
            """;

        var message = QwenOutputParser.ParseAssistant(raw);

        Assert.Equal("I should read it.", message.Reasoning);
        Assert.Equal(string.Empty, message.Content);
        var call = Assert.Single(message.ToolCalls!);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("/tmp/a.txt", call.Arguments["path"]!.GetValue<string>());
    }

    [Fact]
    public void Parse_OpenThinking_EverythingGoesToReasoning()
    {
        var message = QwenOutputParser.ParseAssistant("still thinking about it...");

        Assert.Equal("still thinking about it...", message.Reasoning);
        Assert.Equal(string.Empty, message.Content);
        Assert.False(message.ThinkingClosed);
    }

    [Fact]
    public void Parse_ContentWithMultipleToolCalls()
    {
        const string raw = """
            done thinking</think>Reading both.
            <tool_call>
            <function=read_file>
            <parameter=path>
            a.txt
            </parameter>
            </function>
            </tool_call>
            <tool_call>
            <function=read_file>
            <parameter=path>
            b.txt
            </parameter>
            </function>
            </tool_call>
            """;

        var message = QwenOutputParser.ParseAssistant(raw);

        Assert.Equal("done thinking", message.Reasoning);
        Assert.Equal("Reading both.", message.Content);
        Assert.Equal(2, message.ToolCalls!.Count);
        Assert.Equal("a.txt", message.ToolCalls[0].Arguments["path"]!.GetValue<string>());
        Assert.Equal("b.txt", message.ToolCalls[1].Arguments["path"]!.GetValue<string>());
    }

    [Fact]
    public void Parse_UnclosedToolCall_KeepsContentAndNoCalls()
    {
        // Обрыв генерации посреди tool_call (лимит n_predict): вызовов нет, а текст
        // после маркера не выбрасывается — виден и ответ модели, и место обрыва
        // (парсер ничего не теряет молча).
        const string raw = """
            done thinking</think>Here is what I found.
            <tool_call>
            <function=read_file>
            <parameter=path>
            a.txt
            </parameter>
            """ + "\n";

        var message = QwenOutputParser.ParseAssistant(raw);

        Assert.Null(message.ToolCalls);
        Assert.Equal("done thinking", message.Reasoning);
        Assert.StartsWith("Here is what I found.", message.Content);
        Assert.Contains(QwenSpecialTokens.ToolCallStart, message.Content);
    }

    [Fact]
    public void Parse_StripsImEndToken()
    {
        var message = QwenOutputParser.ParseAssistant("thinking\n</think>\n\nAnswer.<|im_end|>");

        Assert.Equal("Answer.", message.Content);
    }

    [Fact]
    public void RoundTrip_ParsedMessageRendersIdentically()
    {
        // raw — completion модели (без префилла): reasoning, затем think-close, пустая строка, tool_call
        const string raw = """
            I should read it.
            </think>

            <tool_call>
            <function=read_file>
            <parameter=path>
            /tmp/a.txt
            </parameter>
            </function>
            </tool_call>
            <|im_end|>
            """;

        var parsed = QwenOutputParser.ParseAssistant(raw);
        var rendered = QwenChatTemplate.Render(
            new List<ChatMessage> { ChatMessage.User("Read the file."), parsed },
            addGenerationPrompt: false);

        const string expectedTail = """
            <|im_start|>assistant
            <think>
            I should read it.
            </think>

            <tool_call>
            <function=read_file>
            <parameter=path>
            /tmp/a.txt
            </parameter>
            </function>
            </tool_call>
            <|im_end|>
            """ + "\n";

        Assert.EndsWith(expectedTail, rendered.Prompt);
    }

    [Fact]
    public void Parse_ExtractsLeadingStateBlockIntoMessage()
    {
        // think-токены собраны из \x-экранов: литералы системных токенов нельзя
        // писать в аргументы tool_call (парсер чата их съест).
        var state = new StateBlock
        {
            MsgId = 2,
            Time = new DateTime(2026, 8, 17, 13, 15, 26),
            ContextUsed = 12345,
            ContextMax = 32768,
            BuildId = "20260817-102607",
            BuildStatus = "success"
        };
        var raw = state + "\nreal reasoning\n\x3c/think\x3e\n\ncontent here";

        var message = QwenOutputParser.ParseAssistant(raw);

        Assert.Equal(state.ToString(), message.StateBlock?.ToString());
        Assert.Equal("real reasoning", message.Reasoning);
        Assert.Equal("content here", message.Content);
    }

    [Fact]
    public void Parse_WithoutStateBlock_LeavesReasoningUntouched()
    {
        var raw = "just reasoning\n\x3c/think\x3e\n\ncontent here";

        var message = QwenOutputParser.ParseAssistant(raw);

        Assert.Null(message.StateBlock);
        Assert.Equal("just reasoning", message.Reasoning);
        Assert.Equal("content here", message.Content);
    }
}
