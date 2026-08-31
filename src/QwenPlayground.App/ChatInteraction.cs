using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App;

/// <summary>
/// Оконный интерактив инструментов поверх FSM: вопрос (ask_user) и подтверждение опасных
/// команд (shell). Регистрируется в <see cref="AgentInteraction"/> один раз при старте —
/// Core не знает про окна; в тестах/Harness регистрации нет, инструменты деградируют честно.
///
/// Диалоги синхронны (ShowDialog на потоке UI): на время окна FSM стоит в Awaiting*,
/// так что команды и heartbeat корректно видят чат занятым.
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
        AgentInteraction.Ask = AskAsync;
        AgentInteraction.Confirm = ConfirmAsync;
    }

    private Task<string> AskAsync(string question, CancellationToken cancellationToken)
    {
        // FSM: Generating → AwaitingUser → Generating
        _chat.Transition(ChatState.AwaitingUser);
        var window = new Views.QuestionWindow(question)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        var result = window.ShowDialog() == true ? window.Answer : "(пользователь закрыл окно)";
        _chat.Transition(ChatState.Generating);
        return Task.FromResult(result);
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
