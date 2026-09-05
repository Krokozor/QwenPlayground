using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App;

/// <summary>
/// Оконный интерактив инструментов поверх FSM: подтверждение опасных команд (shell).
/// Регистрируется в <see cref="AgentInteraction"/> один раз при старте — Core не знает
/// про окна; в тестах/Harness регистрации нет, инструменты деградируют честно.
///
/// Диалоги синхронны (ShowDialog на потоке UI): на время окна FSM стоит в
/// AwaitingConfirmation, так что команды и heartbeat корректно видят чат занятым.
/// </summary>
public sealed class ChatInteraction
{
    private readonly ChatStateMachine _chat;

    public ChatInteraction(ChatStateMachine chat)
    {
        _chat = chat;
    }

    /// <summary>Зарегистрировать оконных провайдеров как реализацию AgentInteraction.</summary>
    public void Register()
    {
        AgentInteraction.Confirm = ConfirmAsync;
    }

    private Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        // FSM: Generating → AwaitingConfirmation → Generating
        _chat.Transition(ChatState.AwaitingConfirmation);
        var window = new Views.ConfirmWindow(question)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        var result = window.ShowDialog() == true;
        _chat.Transition(ChatState.Generating);
        return Task.FromResult(result);
    }
}
