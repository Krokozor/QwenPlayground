using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Main;
using QwenPlayground.Core.Runtime;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Проекция вида хода агента: события TurnPipeline → пузыри чата, статус, живые
/// реколлы памяти. Доменная оркестрация (бюджет, FSM, AgentLoop, профили, отмена,
/// рестарт) — в TurnPipeline (Core); здесь только перевод событий в видимое.
/// Зависимости — свой рантайм (Log/FSM/Turns/MemorySurfacer) и два общих сервиса
/// (Sessions, Background): окно чата самодостаточно для переноса в отдельное окно.
/// </summary>
public sealed class TurnViewViewModel {
    private readonly ChatRuntime _runtime;
    private readonly SessionController _sessions;
    private readonly BackgroundWork _background;
    private readonly ObservableCollection<MessageViewModel> _messages;
    private readonly Func<string> _sessionDir;
    private readonly Action<string> _status;

    private AppSettings S => AppSettings.Get();

    public TurnViewViewModel(
        ChatRuntime runtime,
        SessionController sessions,
        BackgroundWork background,
        ObservableCollection<MessageViewModel> messages,
        Func<string> sessionDir,
        Action<string> status) {
        _runtime = runtime;
        _sessions = sessions;
        _background = background;
        _messages = messages;
        _sessionDir = sessionDir;
        _status = status;
    }

    public Task GenerateAsync(bool continueLastAssistant = false) =>
        GenerateCoreAsync(continueLastAssistant);

    /// <summary>
    /// Ход: доменная оркестрация (бюджет, FSM, AgentLoop, профили, отмена, рестарт) — в
    /// TurnPipeline; здесь — только состояние вида (пузыри) и решение, куда показать ошибку.
    /// </summary>
    private async Task GenerateCoreAsync(bool continueLastAssistant) {
        var agentic = S.ProjectRoot.Trim().Length > 0;
        var continued = continueLastAssistant && _runtime.Log.Count > 0 &&
            _runtime.Log[^1].Role == ChatRole.Assistant
            ? _runtime.Log[^1]
            : null;
        // Состояние одного хода: локальные мутации обработчиков событий собраны вместе.
        var turn = new TurnState { Continued = continued, Agentic = agentic };
        if (continued is not null) {
            turn.CurrentAssistant = _messages[^1];
            turn.Raw.Append(continued.ToRawOutput());
        }
        var outcome = await _runtime.Turns.RunTurnAsync(continueLastAssistant, e => DispatchEvent(turn, e));
        if (outcome.BudgetFailed) {
            // Бюджет не прошёл — статус и сохранение истории уже сделаны пайплайном.
            return;
        }
        if (outcome.Canceled) {
            // Хвост стрима мог не успеть опубликоваться (троттлинг) — финализируем вид до разбора.
            turn.CurrentAssistant?.FlushStreaming();
            CommitCanceledPartial(turn.Continued, turn.CurrentAssistant, turn.Raw.ToString());
        }
        else if (outcome.Error is { } exception) {
            // В single-режиме исторически показываем ошибку прямо в пузыре ответа.
            if (!outcome.Agentic && turn.CurrentAssistant is not null) {
                turn.CurrentAssistant.Content = $"[ошибка] {exception.Message}";
            }
            else {
                _status($"ошибка: {exception.Message}");
            }
        }
    }

    /// <summary>Состояние одного хода генерации: мутации обработчиков событий собраны здесь.</summary>
    private sealed class TurnState {
        /// <summary>Накопленный сырой вывод (для парсера при отмене и live-реколл).</summary>
        public StringBuilder Raw { get; } = new();
        public MessageViewModel? CurrentAssistant { get; set; }
        public MessageViewModel? PendingTool { get; set; }
        public TokenUsage? Usage { get; set; }
        public ChatMessage? Continued { get; init; }
        public bool Agentic { get; init; }
        /// <summary>BeginStreaming вызван для CurrentAssistant (continue-ход: задан заранее).</summary>
        public bool StreamStarted;
    }

    /// <summary>
    /// Диспетчер событий цикла в состояние хода и вид чата. Новый тип события —
    /// новый case + приватный обработчик; доменная логика остаётся в AgentLoop,
    /// здесь — только перевод в видимое.
    /// </summary>
    private void DispatchEvent(TurnState turn, AgentEvent agentEvent) {
        switch (agentEvent) {
            case TokenEvent token:
                OnToken(turn, token.Text);
                break;
            case AssistantMessageEvent assistant:
                OnAssistantMessage(turn, assistant.Message);
                break;
            case ToolCallStartedEvent started:
                OnToolStarted(turn, started.Name, started.Arguments);
                break;
            case ToolCallFinishedEvent finished:
                OnToolFinished(turn, finished.ToolMessage, finished.Result);
                break;
            case AgentErrorEvent error:
                _status(error.Message);
                break;
            case RestartPendingEvent:
                _status("перезапуск в новую версию...");
                break;
            case NagEvent nag:
                _messages.Add(new MessageViewModel { Role = "user", Content = nag.Text });
                break;
        }
    }

    private void OnToken(TurnState turn, string text) {
        if (turn.CurrentAssistant is null) {
            // Новый стрим: сброс live-реколл окна.
            _runtime.MemorySurfacer.ResetLiveWindow();
            turn.CurrentAssistant = AddAssistantView();
            turn.CurrentAssistant.BeginStreaming(turn.Raw.ToString());
            turn.StreamStarted = true;
        }
        else if (!turn.StreamStarted) {
            // Continue-ход: CurrentAssistant задан в GenerateCoreAsync, но BeginStreaming
            // не вызывался — _streamActive=false, все чанки молча терялись до ApplyParsed.
            turn.CurrentAssistant.BeginStreaming(turn.Raw.ToString());
            turn.StreamStarted = true;
        }
        turn.Raw.Append(text);
        turn.CurrentAssistant.AppendStreamChunk(text);
        _runtime.MemorySurfacer.MaybeFireLiveRecall(turn.Agentic, text, turn.Raw, turn.Continued is not null,
            _runtime.Log, _sessions.CurrentId == MainAgent.SessionId,
            S.CompanionEndpoint, _runtime.Turns.ActiveToken);
    }

    private void OnAssistantMessage(TurnState turn, ChatMessage message) {
        turn.CurrentAssistant ??= AddAssistantView();
        turn.CurrentAssistant.ApplyParsed(message);
        if (message.Generation is { } generation) {
            turn.Usage = new TokenUsage(generation.PromptTokens, generation.CompletionTokens);
        }
        UpdateStatus(turn.Usage);
        turn.CurrentAssistant = null;
        turn.StreamStarted = false;
        turn.Raw.Clear();
        // Ассоциативный реколл: факты подтягиваются между итерациями, фоном на компаньон-модели.
        if (turn.Agentic) {
            var conversation = _runtime.Log;
            var companion = S.CompanionEndpoint;
            var token = _runtime.Turns.ActiveToken;
            _background.Queue("реколл памяти", () =>
                _runtime.MemorySurfacer.RecallAfterTurnAsync(
                    conversation, _sessions.CurrentId == MainAgent.SessionId, companion, token));
        }
    }

    private void OnToolStarted(TurnState turn, string name, JsonObject arguments) {
        turn.PendingTool = new MessageViewModel { Role = "tool", Content = "выполняется..." };
        turn.PendingTool.ToolCalls.Add(MessageViewModel.FormatToolCall(name, arguments));
        turn.PendingTool.ToolCallCount = 1;
        _messages.Add(turn.PendingTool);
    }

    private void OnToolFinished(TurnState turn, ChatMessage toolMessage, string result) {
        if (turn.PendingTool is null) {
            return;
        }
        turn.PendingTool.Content = result;
        // Привязываем фоновое ChatMessage и подгружаем вложения:
        // load_image в FinalizeAsync кладёт файлы в msg_<id> уже после
        // добавления tool-сообщения, поэтому их надо читать по Source.Id.
        turn.PendingTool.Source = toolMessage;
        turn.PendingTool.LoadArtifacts(_sessionDir());
        turn.PendingTool = null;
    }

    private void CommitCanceledPartial(ChatMessage? continued, MessageViewModel? currentAssistant, string raw) {
        if (currentAssistant is null || raw.Trim().Length == 0) {
            return;
        }
        var partial = QwenOutputParser.ParseAssistant(raw);
        partial.ToolCalls = null;
        partial.Generation = null;
        if (continued is not null) {
            continued.Reasoning = partial.Reasoning;
            continued.Content = partial.Content;
            continued.ToolCalls = null;
            continued.ThinkingClosed = partial.ThinkingClosed;
            continued.Generation = null;
            currentAssistant.ApplyParsed(continued);
        }
        else if (_runtime.Log.Count > 0 && _runtime.Log[^1].Role == ChatRole.Assistant) {
            currentAssistant.Source = _runtime.Log[^1];
        }
        else {
            _runtime.Log.Add(partial);
            currentAssistant.ApplyParsed(partial);
        }
    }

    private MessageViewModel AddAssistantView() {
        var view = new MessageViewModel { Role = "assistant" };
        _messages.Add(view);
        return view;
    }

    private void UpdateStatus(TokenUsage? usage) {
        if (usage?.PromptTokens is { } promptTokens)
            _status($"контекст: {promptTokens} токенов (+{usage.CompletionTokens?.ToString() ?? "?"})");
    }
}
