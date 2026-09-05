namespace QwenPlayground.Core.Tools;

/// <summary>
/// Фасад интерактива над скоупом main-агента (<see cref="Runtime.AgentRuntime.Main"/>):
/// историческая точка регистрации UI и статический pull для инструментов, у которых
/// в руках нет <see cref="ToolContext"/>. Единственное хранилище маршрута — скоуп;
/// изолированные агенты получают собственный маршрут через ToolContext.Runtime,
/// не задевая этот фасад.
///
/// Провайдеров регистрирует UI один раз при старте (ChatInteraction.Register):
/// подтверждение опасной команды shell открывает окно поверх чата с переводом FSM
/// в AwaitingConfirmation. В тестах и Harness регистрация отсутствует — интерактивные
/// инструменты честно сообщают «интерактив недоступен» вместо падения.
///
/// Регистрация и вызовы идут на главном потоке (инвариант проекта) — без локировки.
/// </summary>
public static class AgentInteraction
{
    /// <summary>Подтверждение опасного действия (да/нет).</summary>
    public static Func<string, CancellationToken, Task<bool>>? Confirm
    {
        get => Runtime.AgentRuntime.Main.Confirm;
        set => Runtime.AgentRuntime.Main.Confirm = value;
    }

    /// <summary>Подтверждение через зарегистрированного провайдера; null — интерактив недоступен.</summary>
    public static Task<bool>? TryConfirm(string question, CancellationToken cancellationToken) =>
        Runtime.AgentRuntime.Main.TryConfirm(question, cancellationToken);
}
