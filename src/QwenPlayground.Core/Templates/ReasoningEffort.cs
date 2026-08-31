namespace QwenPlayground.Core.Templates;

/// <summary>
/// Усилие размышления (эталонные строки из assets/chat_template.jinja).
/// Типизированный вариант строки "xhigh"/"medium"/"low" — исключает опечатки
/// и невалидные значения на этапе компиляции.
/// </summary>
public enum ReasoningEffort
{
    XHigh,
    Medium,
    Low
}
