namespace QwenPlayground.Core.Tools;

/// <summary>
/// Результат выполнения инструмента + сам экземпляр инструмента (если создан).
/// Экземпляр нужен для опционального этапа финализации (<see cref="AgentTool.FinalizeAsync"/>):
/// после добавления tool-сообщения в разговор вызывается финализация с ID сообщения,
/// чтобы инструмент мог «привязать» себя к своему сообщению (артефакты и т.п.).
/// </summary>
public sealed record ToolExecutionResult(string Text, AgentTool? Tool);