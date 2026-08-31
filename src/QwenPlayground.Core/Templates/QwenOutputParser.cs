using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.MetaInfo;

namespace QwenPlayground.Core.Templates;

public static class QwenOutputParser
{
    // Токены/разметка — из QwenSpecialTokens (единый источник): литералы токенов
    // в исходник не пишем (парсер чата съедает их при редактировании файлов).
    // Мышление всегда включено: think-блок ищется всегда, параметра thinkingOpened нет.
    public static ChatMessage ParseAssistant(string raw)
    {
        var text = raw;

        var imEndIndex = text.IndexOf(QwenSpecialTokens.ImEnd, StringComparison.Ordinal);
        if (imEndIndex >= 0)
        {
            text = text[..imEndIndex];
        }

        string? reasoning = null;
        var thinkingClosed = false;
        var thinkIndex = text.IndexOf(QwenSpecialTokens.ThinkEnd, StringComparison.Ordinal);
        if (thinkIndex >= 0)
        {
            reasoning = text[..thinkIndex].Trim();
            text = text[(thinkIndex + QwenSpecialTokens.ThinkEnd.Length)..];
            thinkingClosed = true;
        }
        else
        {
            reasoning = text.Trim();
            text = string.Empty;
        }

        // State-блок, который шаблон вставляет при рендере, — не мысли модели,
        // а снапшот статуса. Извлекаем в message.StateBlock, чтобы он персистировался
        // с сообщением и рендерился в истории.
        var (stateBlock, rest) = StateBlock.SplitLeading(reasoning);
        reasoning = rest.Length == 0 ? null : rest;

        var toolCalls = new List<ToolCall>();
        // Позиция первого ЗАКРЫТОГО tool_call-маркера: контент обрезается по ней только
        // если блок реально распарсен в вызов. Незакрытый блок (обрыв стрима посреди
        // tool_call) не должен выбрасывать весь текст после маркера — это был бы
        // «ответ без ответа»: и вызовов нет, и content пуст.
        var contentEnd = -1;
        var searchFrom = 0;

        while (true)
        {
            var open = text.IndexOf(QwenSpecialTokens.ToolCallStart, searchFrom, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }
            var innerStart = open + QwenSpecialTokens.ToolCallStart.Length;
            var close = text.IndexOf(QwenSpecialTokens.ToolCallEnd, innerStart, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }
            if (contentEnd < 0)
            {
                contentEnd = open;
            }
            if (TryParseFunction(text[innerStart..close], out var call))
            {
                toolCalls.Add(call);
            }
            searchFrom = close + QwenSpecialTokens.ToolCallEnd.Length;
        }

        return new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = text[..(contentEnd >= 0 ? contentEnd : text.Length)].Trim(),
            Reasoning = reasoning,
            StateBlock = stateBlock,
            ThinkingClosed = thinkingClosed,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null
        };
    }

    private static bool TryParseFunction(string block, out ToolCall call)
    {
        call = null!;

        var start = block.IndexOf(QwenSpecialTokens.FunctionStartPrefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }
        start += QwenSpecialTokens.FunctionStartPrefix.Length;

        var nameEnd = block.IndexOf('>', start);
        if (nameEnd < 0)
        {
            return false;
        }
        var name = block[start..nameEnd];

        var arguments = new JsonObject();
        var cursor = nameEnd + 1;

        while (true)
        {
            var paramStart = block.IndexOf(QwenSpecialTokens.ParameterStartPrefix, cursor, StringComparison.Ordinal);
            if (paramStart < 0)
            {
                break;
            }
            paramStart += QwenSpecialTokens.ParameterStartPrefix.Length;

            var paramNameEnd = block.IndexOf('>', paramStart);
            if (paramNameEnd < 0)
            {
                break;
            }
            var paramName = block[paramStart..paramNameEnd];

            var paramClose = block.IndexOf(QwenSpecialTokens.ParameterEnd, paramNameEnd, StringComparison.Ordinal);
            if (paramClose < 0)
            {
                break;
            }

            var value = block[(paramNameEnd + 1)..paramClose];
            if (value.StartsWith('\n'))
            {
                value = value[1..];
            }
            if (value.EndsWith('\n'))
            {
                value = value[..^1];
            }

            arguments[paramName] = value;
            cursor = paramClose + QwenSpecialTokens.ParameterEnd.Length;
        }

        call = new ToolCall { Name = name, Arguments = arguments };
        return true;
    }
}