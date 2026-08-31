using System.Collections.Generic;

namespace QwenPlayground.Core.Chat;

/// <summary>
/// Инъекция динамического системного промпта: ведущее system-сообщение подменяется
/// (или вставляется в начало, если его нет), исходный список не мутируется.
/// Единая семантика для AgentLoop.systemPromptProvider (каждый рендер хода) и
/// MainViewModel.BuildRenderMessages (предпросмотр и подсчёт бюджета).
/// </summary>
public static class SystemPromptInjection
{
    public static List<ChatMessage> Apply(IReadOnlyList<ChatMessage> messages, string systemContent)
    {
        var result = new List<ChatMessage>(messages);
        if (result.Count > 0 && result[0].Role == ChatRole.System)
        {
            result[0] = ChatMessage.System(systemContent);
        }
        else
        {
            result.Insert(0, ChatMessage.System(systemContent));
        }
        return result;
    }
}
