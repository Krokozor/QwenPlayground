using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Tests;

public sealed class StructuredCompletionTests
{
    [Fact]
    public void Render_ContainsOneToolAndInstruction_AndNoStateNote()
    {
        var prompt = StructuredCompletion.Render("Сделай резюме", "system-text");

        Assert.Contains(StructuredCompletion.ToolName, prompt);
        Assert.Contains("You have access to the following functions:", prompt);
        Assert.Contains("Submit your result by calling the submit_result tool", prompt);
        Assert.StartsWith("<|im_start|>system", prompt);
        // Сервисный вызов без state-блока: заметка о нём не должна попадать в промпт.
        Assert.DoesNotContain("prefill of system status", prompt);
    }

    [Fact]
    public void Render_DoesNotBiasReasoningEffort()
    {
        // Medium намеренно: в эталонном шаблоне для него нет инструкции «думай больше/меньше».
        // Сервисный вызов не должен подталкивать модель к длинным размышлениям (риск съесть бюджет)
        // и не должен заставлять её думать слишком кратко (риск ошибок).
        var prompt = StructuredCompletion.Render("сделай резюме");

        Assert.DoesNotContain("Reasoning effort is set to xhigh", prompt);
        Assert.DoesNotContain("Reasoning effort is set to low", prompt);
    }

    [Fact]
    public void Render_AppendsPrefillInsideThinkBlock()
    {
        // Prefill — страховка для низких квантов: модель «уже начала» мысль с намерением
        // вызвать submit_result (баг-репорт: суммаризация не всегда проходит, модель
        // забывает про инструмент).
        var prompt = StructuredCompletion.Render("сделай резюме");

        Assert.EndsWith("I am working out the result. Then I will call the submit_result tool with the complete result.\n", prompt);
        // Prefill — ПОСЛЕ открытия think-блока (внутри мысли модели).
        var thinkStart = prompt.LastIndexOf(QwenSpecialTokens.ThinkStart, StringComparison.Ordinal);
        var prefillIndex = prompt.LastIndexOf("I am working out the result", StringComparison.Ordinal);
        Assert.True(thinkStart >= 0 && prefillIndex > thinkStart);
    }

    [Fact]
    public void ExtractResult_ReturnsToolParameter()
    {
        var raw =
            QwenSpecialTokens.ThinkStart + "\nобдумываю задачу\n" +
            QwenSpecialTokens.ThinkEnd + "\n\n" +
            "<tool_call>\n<function=submit_result>\n<parameter=result>\nИТОГ СУММАРИЗАЦИИ\n</parameter>\n</function>\n</tool_call>\n";

        Assert.Equal("ИТОГ СУММАРИЗАЦИИ", StructuredCompletion.ExtractResult(raw));
    }

    [Fact]
    public void ExtractResult_FallsBackToFreeText_WhenNoToolCall()
    {
        // Низкий квант не вызвал инструмент, но записал результат текстом после
        // закрытого think-блока — принимаем (раньше это было null → прерванная компакция).
        var raw = QwenSpecialTokens.ThinkStart + "\nx\n" + QwenSpecialTokens.ThinkEnd + "\nответ просто текстом";

        Assert.Equal("ответ просто текстом", StructuredCompletion.ExtractResult(raw));
    }

    [Fact]
    public void ExtractResult_ReturnsNull_WhenThinkNotClosed()
    {
        // Незакрытый think = обрыв генерации, не ответ: фолбэк не применяется.
        var raw = QwenSpecialTokens.ThinkStart + "\nдумаю, но не закончил";

        Assert.Null(StructuredCompletion.ExtractResult(raw));
    }

    [Fact]
    public void ExtractResult_ReturnsNull_ForEmptyResult()
    {
        var raw =
            QwenSpecialTokens.ThinkEnd + "\n" +
            "<tool_call>\n<function=submit_result>\n<parameter=result>\n   \n</parameter>\n</function>\n</tool_call>\n";

        Assert.Null(StructuredCompletion.ExtractResult(raw));
    }
}