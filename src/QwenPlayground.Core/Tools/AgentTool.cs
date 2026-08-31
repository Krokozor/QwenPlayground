namespace QwenPlayground.Core.Tools;

public abstract class AgentTool
{
    /// <summary>
    /// Основной этап: выполнение инструмента. Результат — текст, который станет
    /// content-ом tool-сообщения.
    /// </summary>
    public abstract Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Опциональный этап финализации: вызывается после того, как результат инструмента
    /// добавлен в разговор как tool-сообщение и получил стабильный ID. Здесь инструмент
    /// знает свой чат (<see cref="ToolContext.Conversation"/>) и ID собственного сообщения
    /// (<paramref name="messageId"/>) и может «привязать» себя к сообщению — например,
    /// перенести артефакты в каталог msg_&lt;id&gt;. По умолчанию no-op.
    /// </summary>
    public virtual Task FinalizeAsync(ToolContext context, int messageId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
