using System.Text;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Memory;

/// <summary>
/// Ассоциативный реколл main-агента: всплывшие факты складываются в state-блок, живут до
/// компакции, дубликаты по id не повторяются. Работает на фоне, на компаньон-модели,
/// fire-and-forget — ошибки глотаются (не критичный путь генерации).
///
/// Два режима:
///  - пост-ходовой (<see cref="RecallAfterTurnAsync"/>): факты сразу подтверждены
///    (Sightings = порог стабильности);
///  - live (<see cref="RecallLiveAsync"/> / <see cref="MaybeFireLiveRecall"/>): во время
///    генерации, по «живому» окну (история + партиал текущего хода); факты с Sightings=1 —
///    правило стабильности не выпустит их в state-блок, пока не подтвердятся повторным сэмплом.
///
/// Также владеет нагом менеджмента памяти: периодически дёргает модель заняться дедупом
/// (сбрасывается, когда модель сама задела memory_*-инструмент).
/// </summary>
public sealed class MemorySurfacer
{
    private readonly List<SurfacedMemory> _surfaced = new();
    private bool _recallInFlight;
    private int _iterationsSinceMemoryMgmt;
    private int _liveStreamedTokens;
    private DateTime _liveLastFireAt = DateTime.MinValue;

    // Правило стабильности: факты, всплывшие «слишком рано», не выходят в контекст, пока
    // не подтвердятся повторным сэмплом.
    private static int SurfacingThreshold => AppSettings.Get().MemorySurfacingThreshold;
    // Live-реколл: не чаще LiveRecallInterval и только если с прошлой пробы натекло
    // >= LiveRecallMinTokens.
    private static int LiveRecallMinTokens => AppSettings.Get().MemoryLiveRecallMinTokens;
    private static TimeSpan LiveRecallInterval => TimeSpan.FromSeconds(AppSettings.Get().MemoryLiveRecallIntervalSec);
    // Наг менеджмента памяти: модель в обсессии не займётся дедупом сама — дёргаем.
    private static int MemoryMgmtNagInterval => AppSettings.Get().MemoryNagIntervalRenders;
    // Параметры реколла (см. MemoryRecall.RecallAsync).
    private static int TopX => AppSettings.Get().RecallTopX;
    private static double MinScore => AppSettings.Get().RecallMinScore;

    /// <summary>Наг для state-блока (null, пока не наступил интервал; память выключена — всегда null).</summary>
    public string? MemoryNag => AppSettings.Get().MemoryEnabled
        && _iterationsSinceMemoryMgmt >= MemoryMgmtNagInterval
        ? "If you finished the current stage, do memory management: call memory_list to spot duplicates " +
          "and memory_merge / memory_delete to consolidate. Otherwise ignore this."
        : null;

    /// <summary>Всплывшие факты для state-блока (правило стабильности применено; память выключена — пусто).</summary>
    public IReadOnlyList<SurfacedMemory> GetSurfacedForStateBlock()
    {
        if (!AppSettings.Get().MemoryEnabled)
        {
            return [];
        }
        lock (_surfaced)
        {
            return _surfaced.Where(m => m.Sightings >= SurfacingThreshold).ToList();
        }
    }

    /// <summary>Каждый рендер state-блока: сдвигаем счётчик нага менеджмента памяти.</summary>
    public void OnRendered() => _iterationsSinceMemoryMgmt++;

    /// <summary>Новый стрим: сброс окна live-реколла (счётчик токенов + время последней пробы).</summary>
    public void ResetLiveWindow()
    {
        _liveStreamedTokens = 0;
        _liveLastFireAt = DateTime.MinValue;
    }

    /// <summary>
    /// Свежая собственная запись агента (memory_add): попадает в пул сразу ПОДТВЕРЖДЁННОЙ
    /// (Sightings = порогу стабильности) — агент увидит её в следующем state-блоке,
    /// петля «написал → видит» замыкается без ожидания реколла.
    /// </summary>
    public void SurfaceOwnWrite(string id, string content)
    {
        lock (_surfaced)
        {
            _surfaced.RemoveAll(m => m.Id == id);
            _surfaced.Add(new SurfacedMemory(id, content, Score: 1.0)
            {
                Sightings = SurfacingThreshold
            });
        }
    }

    /// <summary>Модель задела memory_*-инструмент: сбрасываем счётчик нага.</summary>
    public void OnMemoryToolUsed() => _iterationsSinceMemoryMgmt = 0;

    /// <summary>Всплывшие воспоминания выпадают из контекста на компакции.</summary>
    public void Clear()
    {
        lock (_surfaced)
        {
            _surfaced.Clear();
        }
    }

    /// <summary>Пост-ходовой реколл: факты сразу подтверждены.</summary>
    public Task RecallAfterTurnAsync(IReadOnlyList<ChatMessage> conversation, bool isMainSession,
        string endpoint, CancellationToken cancellationToken)
        => RecallCoreAsync(DialogueWindow.Build(conversation), isMainSession, endpoint,
            cancellationToken, startsConfirmed: true);

    /// <summary>Live-реколл по «живому» окну (история + партиал текущего хода).</summary>
    public Task RecallLiveAsync(IReadOnlyList<ChatMessage> conversation, string pendingRaw,
        bool isContinuation, bool isMainSession, string endpoint, CancellationToken cancellationToken)
        => RecallCoreAsync(
            DialogueWindow.Build(BuildLiveWindow(conversation, pendingRaw, isContinuation)),
            isMainSession, endpoint, cancellationToken, startsConfirmed: false);

    /// <summary>
    /// Триггер live-реколла по мере стриминга: не чаще LiveRecallInterval и только когда
    /// с прошлой пробы натекло >= LiveRecallMinTokens. Гениальный think длиной в тысячи токенов
    /// обрабатывается в процессе, а короткий ответ живёт обычным путём (recall после хода).
    /// </summary>
    public void MaybeFireLiveRecall(bool agentic, string chunk, StringBuilder raw, bool isContinuation,
        IReadOnlyList<ChatMessage> conversation, bool isMainSession, string endpoint,
        CancellationToken cancellationToken)
    {
        if (!agentic || raw.Length == 0)
        {
            return;
        }
        _liveStreamedTokens += Math.Max(1, chunk.Length / 4);
        var now = DateTime.Now;
        if (_liveStreamedTokens < LiveRecallMinTokens ||
            _liveLastFireAt != DateTime.MinValue && now - _liveLastFireAt < LiveRecallInterval)
        {
            return;
        }
        _liveStreamedTokens = 0;
        _liveLastFireAt = now;
        _ = RecallLiveAsync(conversation, raw.ToString(), isContinuation, isMainSession, endpoint, cancellationToken);
    }

    private async Task RecallCoreAsync(string context, bool isMainSession, string endpoint,
        CancellationToken cancellationToken, bool startsConfirmed)
    {
        // Память выключена вручную — реколл (post-turn и live) не запускаем вообще.
        if (!AppSettings.Get().MemoryEnabled || context.Length == 0 || _recallInFlight || !isMainSession)
        {
            return;
        }
        _recallInFlight = true;
        try
        {
            var hits = await MemoryRecall.RecallAsync(
                context, new MemoryStore(), endpoint, TopX, MinScore, rerank: true, cancellationToken);
            lock (_surfaced)
            {
                foreach (var hit in hits)
                {
                    var existing = _surfaced.FirstOrDefault(s => s.Id == hit.Item.Id);
                    if (existing is not null)
                    {
                        existing.Sightings++;
                        continue;
                    }
                    _surfaced.Add(new SurfacedMemory(
                        hit.Item.Id, hit.Item.Content, hit.Score)
                    {
                        Sightings = startsConfirmed ? SurfacingThreshold : 1
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // реколл — не критичный путь
        }
        finally
        {
            _recallInFlight = false;
        }
    }

    private static IEnumerable<ChatMessage> BuildLiveWindow(IReadOnlyList<ChatMessage> conversation,
        string pendingRaw, bool isContinuation)
    {
        // Стрим текущего хода в _conversation не попадает, пока ход не завершён; партиал
        // склеиваем в отдельное ассистентское сообщение (в continuation — ПОСЛЕ места, где он
        // уже лежит в истории со старой версией контента, поэтому последнее отбрасываем).
        var skipLast = isContinuation && conversation.Count > 0 && conversation[^1].Role == ChatRole.Assistant;
        var window = new List<ChatMessage>(skipLast ? conversation.Take(conversation.Count - 1) : conversation);
        window.Add(ChatMessage.Assistant(pendingRaw));
        return window;
    }
}
