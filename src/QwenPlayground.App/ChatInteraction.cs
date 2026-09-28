using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;
using QwenPlayground.App.ViewModels;

namespace QwenPlayground.App;

/// <summary>
/// Оконный интерактив инструментов поверх FSM: подтверждение опасных команд (shell).
/// Регистрируется в <see cref="AgentInteraction"/> один раз при старте — Core не знает
/// про окна; в тестах/Harness регистрации нет, инструменты деградируют честно.
///
/// Подтверждение — карточка в чате (фаза 1.5, план 2026-09-28), а не модалка: здесь
/// только FSM-рамка (Generating → AwaitingConfirmation → Generating), ожидание решения
/// идёт асинхронно, UI-поток свободен. Карточку хостит окно рантайма, который попросил
/// подтверждения (main — через ChatViewModel; пер-оконные скоупы — фаза 2).
/// </summary>
public sealed class ChatInteraction
{
    private readonly ChatStateMachine _chat;
    private readonly Func<ChatViewModel> _chatWindow;

    public ChatInteraction(ChatStateMachine chat, Func<ChatViewModel> chatWindow)
    {
        _chat = chat;
        _chatWindow = chatWindow;
    }

    /// <summary>Зарегистрировать оконных провайдеров как реализацию AgentInteraction.</summary>
    public void Register()
    {
        AgentInteraction.Confirm = ConfirmAsync;
    }

    private async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        // FSM: Generating → AwaitingConfirmation → Generating. Пока карточка ждёт,
        // IsBusy=true: команды, heartbeat и wake корректно видят чат занятым.
        _chat.Transition(ChatState.AwaitingConfirmation);
        try
        {
            return await _chatWindow().RequestConfirmationAsync(question, cancellationToken);
        }
        finally
        {
            // Возврат только если FSM ещё ждёт (RestartPending — терминален, не бросаем).
            if (_chat.Current == ChatState.AwaitingConfirmation)
            {
                _chat.Transition(ChatState.Generating);
            }
        }
    }
}
