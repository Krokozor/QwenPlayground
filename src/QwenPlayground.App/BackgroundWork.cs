using QwenPlayground.Core.Runtime;

namespace QwenPlayground.App;

/// <summary>
/// Владелец фоновой работы приложения — мини-аналог BotTaskProcessor из NekoBot,
/// без очереди: конкурентность не нужна, FSM и внутренние гварды уже сериализуют работу.
///
/// Каждая работа регистрируется в <see cref="TurnRegistry"/> (личность: имя, состояние,
/// журнал, отмена) — UI-диспетчер рисует список ходов вместо одной статусной строки.
/// Политика отчёта прежняя: OperationCanceledException — штатный путь (Stop/закрытие/
/// смена сессии), глотается; прочие исключения попадают в отчётчик (статус-строка)
/// с именем работы. Исполнение на потоке UI (инвариант проекта).
/// </summary>
public sealed class BackgroundWork
{
    private readonly Action<string> _report;
    private readonly TurnRegistry _turns;

    public BackgroundWork(Action<string> report, TurnRegistry? turns = null)
    {
        _report = report;
        _turns = turns ?? new TurnRegistry();
    }

    /// <summary>Реестр ходов этого исполнителя (UI подписывается на Changed).</summary>
    public TurnRegistry Turns => _turns;

    /// <summary>Запустить работу в фоне под присмотром: имя попадёт в статус при падении.</summary>
    public void Queue(string name, Func<Task> work) => _ = RunAsync(name, _ => work());

    /// <summary>
    /// То же с поддержкой отмены: работа получает токен хода (Cancel из реестра/UI
    /// или общий Stop). Работы без токена — перегрузка выше.
    /// </summary>
    public void Queue(string name, Func<CancellationToken, Task> work) => _ = RunAsync(name, work);

    /// <summary>
    /// Тело присмотра, публичное для тестов (Queue — fire-and-forget). Возвращает задачу,
    /// завершающуюся вместе с ходом: терминальное состояние записано гарантированно.
    /// </summary>
    public async Task RunAsync(string name, Func<CancellationToken, Task> work)
    {
        var turn = _turns.Register(name);
        turn.Log("запуск");
        turn.Begin();
        try
        {
            await work(turn.Cancellation.Token);
            turn.Finish(TurnState.Succeeded);
        }
        catch (OperationCanceledException)
        {
            // Отмена — не ошибка: Stop/закрытие/смена сессии. Состояние фиксируется в реестре.
            turn.Finish(TurnState.Canceled);
        }
        catch (Exception exception)
        {
            turn.Finish(TurnState.Failed, exception.Message);
            _report($"⚠ {name}: {exception.Message}");
        }
    }
}
