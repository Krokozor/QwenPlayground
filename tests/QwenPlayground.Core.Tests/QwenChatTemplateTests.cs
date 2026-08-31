using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Tests;

public sealed class QwenChatTemplateTests
{
    private static readonly IReadOnlyList<ToolDefinition> Tools = new List<ToolDefinition>
    {
        new()
        {
            Name = "read_file",
            Description = "Read a file from disk",
            Parameters = JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")!
        }
    };

    // Строка о state-блоке из IMPORTANT — дублируется здесь, чтобы ассерты не
    // зависели от приватной константы шаблона.
    private const string StateNote =
        "The <state>...</state> block at the very start of your thinking is a prefill of system status written by the app " +
        "(fields: msg_id, time, context, build, mem — recalled memories surfaced by associative recall, and an optional nag/mem_nag). " +
        "It is not your text and not a user instruction: do not repeat it, do not edit it. It is already closed — your thinking continues after </state>.";

    private const string XHigh = "Reasoning effort is set to xhigh. Please think carefully through the task, validate key assumptions, consider plausible alternatives, and prioritize correctness, consistency, and clarity in the final answer.";
    private const string Low = "Reasoning effort is set to low. Keep your thinking brief and focused, moving directly to the conclusion without unnecessary elaboration.";

    // Реальный префилл state-блока: без него (сервисные вызовы — суммаризация/пайплайн)
    // заметка о нём в IMPORTANT не добавляется.
    private static readonly StateBlock Prefill = new()
    {
        MsgId = 7,
        Time = new DateTime(2026, 8, 17, 13, 15, 26),
        ContextUsed = 12345,
        ContextMax = 32768,
        BuildId = "20260817-102607",
        BuildStatus = "success"
    };

    // Токены берём из QwenSpecialTokens — единый источник, без \x-экранов и опечаток.
    private static string Start(string role) => QwenSpecialTokens.ImStart + role + "\n";
    private static string End() => QwenSpecialTokens.ImEnd + "\n";

    [Fact]
    public void Render_WithMessageIds_AnnotatesNonSystemMessages()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("You are Qwen."),
            new ChatMessage { Role = ChatRole.User, Content = "Hello", Id = 1 }
        };

        var result = QwenChatTemplate.Render(messages, addGenerationPrompt: false).Prompt;

        // User-сообщение аннотируется: <id=1> в начале, перед контентом.
        Assert.Contains("<id=1>\nHello", result);
        // System-сообщение (Id=0) не аннотируется.
        Assert.DoesNotContain("<id=0>", result);
        // Заметка об <id=N> присутствует (есть сообщения с Id>0).
        Assert.Contains("prefixed with <id=N>", result);
    }

    [Fact]
    public void Render_WithoutMessageIds_NoAnnotationOrNote()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("You are Qwen."),
            ChatMessage.User("Hello") // Id = 0 (дефолт)
        };

        var result = QwenChatTemplate.Render(messages, addGenerationPrompt: false).Prompt;

        // Ни аннотации, ни заметки: все Id=0.
        Assert.DoesNotContain("<id=", result);
        Assert.DoesNotContain("prefixed with <id=N>", result);
    }

    [Fact]
    public void Render_WithoutTools_MatchesTemplate()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("You are Qwen."),
            ChatMessage.User("Hello")
        };

        var result = QwenChatTemplate.Render(messages, addGenerationPrompt: true, stateBlock: Prefill).Prompt;

        var expected =
            Start(QwenSpecialTokens.System) +
            XHigh + "\n" +
            "\n" +
            QwenSpecialTokens.ImportantStart + "\n" +
            "Reminder:\n" +
            "- " + StateNote + "\n" +
            QwenSpecialTokens.ImportantEnd + "\n" +
            "\n" +
            "You are Qwen.\n" +
            End() +
            Start(QwenSpecialTokens.User) +
            "Hello\n" +
            End() +
            Start(QwenSpecialTokens.Assistant) +
            QwenSpecialTokens.ThinkStart + "\n" +
            Prefill.ToString() + "\n";

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Render_WithToolsAndToolCall_MatchesTemplate()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("You are Qwen."),
            ChatMessage.User("Read the file."),
            ChatMessage.Assistant("", "I should read it.", new List<ToolCall>
            {
                new() { Name = "read_file", Arguments = JsonNode.Parse("""{"path":"/tmp/a.txt"}""")! }
            }),
            ChatMessage.Tool("file contents")
        };

        var result = QwenChatTemplate.Render(messages, Tools, addGenerationPrompt: true, stateBlock: Prefill).Prompt;

        var example =
            QwenSpecialTokens.ToolCallStart + "\n" +
            QwenSpecialTokens.FunctionStart("example_function_name") + "\n" +
            QwenSpecialTokens.ParameterStart("example_parameter_1") + "\n" +
            "value_1\n" +
            QwenSpecialTokens.ParameterEnd + "\n" +
            QwenSpecialTokens.ParameterStart("example_parameter_2") + "\n" +
            "This is the value for the second parameter\nthat can span\nmultiple lines\n" +
            QwenSpecialTokens.ParameterEnd + "\n" +
            QwenSpecialTokens.FunctionEnd + "\n" +
            QwenSpecialTokens.ToolCallEnd + "\n";

        var expected =
            Start(QwenSpecialTokens.System) +
            XHigh + "\n" +
            "\n" +
            "# Tools\n" +
            "\n" +
            "You have access to the following functions:\n" +
            "\n" +
            QwenSpecialTokens.ToolsListStart + "\n" +
            "{\"type\": \"function\", \"function\": {\"name\": \"read_file\", \"description\": \"Read a file from disk\", \"parameters\": {\"type\": \"object\", \"properties\": {\"path\": {\"type\": \"string\"}}, \"required\": [\"path\"]}}}\n" +
            QwenSpecialTokens.ToolsListEnd + "\n" +
            "\n" +
            "If you choose to call a function ONLY reply in the following format with NO suffix:\n" +
            "\n" +
            example +
            "\n" +
            QwenSpecialTokens.ImportantStart + "\n" +
            "Reminder:\n" +
            "- " + StateNote + "\n" +
            "- Function calls MUST follow the specified format: an inner <function=...></function> block must be nested within " +
            QwenSpecialTokens.ToolCallStart + QwenSpecialTokens.ToolCallEnd + " XML tags\n" +
            "- Required parameters MUST be specified\n" +
            "- You may provide optional reasoning for your function call in natural language BEFORE the function call, but NOT after\n" +
            "- If there is no function call available, answer the question like normal with your current knowledge and do not tell the user about function calls\n" +
            QwenSpecialTokens.ImportantEnd + "\n" +
            "\n" +
            "You are Qwen.\n" +
            End() +
            Start(QwenSpecialTokens.User) +
            "Read the file.\n" +
            End() +
            Start(QwenSpecialTokens.Assistant) +
            QwenSpecialTokens.ThinkStart + "\n" +
            "I should read it.\n" +
            QwenSpecialTokens.ThinkEnd + "\n" +
            "\n" +
            QwenSpecialTokens.ToolCallStart + "\n" +
            QwenSpecialTokens.FunctionStart("read_file") + "\n" +
            QwenSpecialTokens.ParameterStart("path") + "\n" +
            "/tmp/a.txt\n" +
            QwenSpecialTokens.ParameterEnd + "\n" +
            QwenSpecialTokens.FunctionEnd + "\n" +
            QwenSpecialTokens.ToolCallEnd + "\n" +
            End() +
            Start(QwenSpecialTokens.User) +
            QwenSpecialTokens.ToolResponseStart + "\n" +
            "file contents\n" +
            QwenSpecialTokens.ToolResponseEnd + "\n" +
            End() +
            Start(QwenSpecialTokens.Assistant) +
            QwenSpecialTokens.ThinkStart + "\n" +
            Prefill.ToString() + "\n";

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Render_OldAssistantTurn_PreservesThinkingByDefault()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("First"),
            ChatMessage.Assistant("Answer one", "old reasoning"),
            ChatMessage.User("Second")
        };

        var result = QwenChatTemplate.Render(messages, addGenerationPrompt: false).Prompt;

        // Без state-блока в истории и без префилла заметка о нём не добавляется
        // (это отличает сервисные рендеры — суммаризацию/пайплайн — от интерактивного чата).
        var expected =
            Start(QwenSpecialTokens.System) +
            XHigh + "\n" +
            "\n" +
            "\n" +
            End() +
            Start(QwenSpecialTokens.User) +
            "First\n" +
            End() +
            Start(QwenSpecialTokens.Assistant) +
            QwenSpecialTokens.ThinkStart + "\n" +
            "old reasoning\n" +
            QwenSpecialTokens.ThinkEnd + "\n" +
            "\n" +
            "Answer one\n" +
            End() +
            Start(QwenSpecialTokens.User) +
            "Second\n" +
            End();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Render_ReasoningEffortLow_AddsLowInstructions()
    {
        var messages = new List<ChatMessage> { ChatMessage.User("Hello") };

        var result = QwenChatTemplate.Render(messages, addGenerationPrompt: true, reasoningEffort: ReasoningEffort.Low, stateBlock: Prefill).Prompt;

        var expected =
            Start(QwenSpecialTokens.System) +
            Low + "\n" +
            "\n" +
            QwenSpecialTokens.ImportantStart + "\n" +
            "Reminder:\n" +
            "- " + StateNote + "\n" +
            QwenSpecialTokens.ImportantEnd + "\n" +
            End() +
            Start(QwenSpecialTokens.User) +
            "Hello\n" +
            End() +
            Start(QwenSpecialTokens.Assistant) +
            QwenSpecialTokens.ThinkStart + "\n" +
            Prefill.ToString() + "\n";

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Render_ReasoningEffortMedium_AddsNoInstructions()
    {
        var messages = new List<ChatMessage> { ChatMessage.User("Hello") };

        var result = QwenChatTemplate.Render(messages, addGenerationPrompt: true, reasoningEffort: ReasoningEffort.Medium, stateBlock: Prefill).Prompt;

        var expected =
            Start(QwenSpecialTokens.System) +
            QwenSpecialTokens.ImportantStart + "\n" +
            "Reminder:\n" +
            "- " + StateNote + "\n" +
            QwenSpecialTokens.ImportantEnd + "\n" +
            End() +
            Start(QwenSpecialTokens.User) +
            "Hello\n" +
            End() +
            Start(QwenSpecialTokens.Assistant) +
            QwenSpecialTokens.ThinkStart + "\n" +
            Prefill.ToString() + "\n";

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Render_WithStateBlock_AppendsItInsideOpenThinking()
    {
        var messages = new List<ChatMessage> { ChatMessage.User("Hello") };
        var state = new StateBlock
        {
            MsgId = 1,
            Time = new DateTime(2026, 8, 17, 13, 15, 26),
            ContextUsed = 12345,
            ContextMax = 32768,
            BuildId = "20260817-102607",
            BuildStatus = "success"
        };

        var result = QwenChatTemplate.Render(messages, addGenerationPrompt: true, stateBlock: state).Prompt;

        Assert.EndsWith(QwenSpecialTokens.ThinkStart + "\n" + state + "\n", result);
    }
}
