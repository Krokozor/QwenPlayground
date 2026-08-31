using QwenPlayground.Core.Chat;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Инъекция динамического системного промпта: единая семантика для AgentLoop (реальный ход)
/// и MainViewModel (предпросмотр/бюджет) — см. SystemPromptInjection.
/// </summary>
public sealed class SystemPromptInjectionTests
{
    [Fact]
    public void Apply_ReplacesLeadingSystemMessage()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("старый системный"),
            ChatMessage.User("привет")
        };

        var result = SystemPromptInjection.Apply(messages, "новый системный");

        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.Equal("новый системный", result[0].Content);
        Assert.Equal("привет", result[1].Content);
    }

    [Fact]
    public void Apply_InsertsSystem_WhenAbsent()
    {
        var messages = new List<ChatMessage> { ChatMessage.User("привет") };

        var result = SystemPromptInjection.Apply(messages, "системный");

        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.Equal("системный", result[0].Content);
        Assert.Equal("привет", result[1].Content);
    }

    [Fact]
    public void Apply_DoesNotMutateOriginalList()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("старый"),
            ChatMessage.User("u")
        };
        var originalFirst = messages[0];

        _ = SystemPromptInjection.Apply(messages, "новый");

        Assert.Same(originalFirst, messages[0]);
        Assert.Equal("старый", messages[0].Content);
        Assert.Equal(2, messages.Count); // исходный список не тронут
    }

    [Fact]
    public void Apply_EmptyList_GetsSingleSystemMessage()
    {
        var result = SystemPromptInjection.Apply([], "системный");

        Assert.Single(result);
        Assert.Equal(ChatRole.System, result[0].Role);
    }
}
