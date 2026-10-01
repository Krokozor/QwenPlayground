using System.Collections.Concurrent;
using QwenPlayground.Core.Runtime;

namespace QwenPlayground.Core.Inference;

/// <summary>
/// Когда приложение ПОСЛЕДНИЙ РАЗ использовало слот и КТО (для метки «застыл» и
/// владельца в «Диагностике»): слот с KV, который ходы не трогают дольше часа, — мусор,
/// кандидат на ручную чистку; владелец (id сессии / «пробы») показывает, ЧЕЙ это KV.
/// Записывается в цикле хода (AgentLoop — знает и слот, и сессию) и в пробах
/// (LlmProbeClient) — так «последнее использование» = «последний реальный запрос».
/// Статика: данные живы пока жив процесс; после рестарта приложения метка
/// «застыл» не ставится (последнее использование неизвестно — не гадать).
/// </summary>
public static class SlotUsageTracker
{
    /// <summary>Факт использования: когда и кем (id сессии хода или «пробы»).</summary>
    public sealed record SlotUse(DateTime Time, string Owner);

    private static readonly ConcurrentDictionary<int, SlotUse> _lastUse = new();

    public static void Record(int slotId, string owner)
    {
        // ConcurrentDictionary прощает гонку, но сам инвариант (ход/пробы — на UI-потоке)
        // проверяем: «последнее использование» — данные диагностики, порча потока меняет смысл.
        UiThreadPolicy.Assert($"SlotUsageTracker.Record(slot {slotId}, {owner})");
        _lastUse[slotId] = new SlotUse(DateTime.Now, owner);
    }

    public static SlotUse? LastUse(int slotId) => _lastUse.TryGetValue(slotId, out var use) ? use : null;
}
