using System.Collections.Concurrent;

namespace QwenPlayground.Core.Inference;

/// <summary>
/// Когда приложение ПОСЛЕДНИЙ РАЗ использовало слот (для метки «застыл» в «Диагностике»):
/// слот с KV, который ходы не трогают дольше часа, — мусор, кандидат на ручную чистку.
/// Записывается в единственной точке, где id_slot кладётся в запрос (LlmCompletionClient)
/// и в пробах (LlmProbeClient) — так «последнее использование» = «последний реальный запрос»,
/// а не попытка. Статика: данные живы пока жив процесс; после рестарта приложения метка
/// «застыл» не ставится (последнее использование неизвестно — не гадать).
/// </summary>
public static class SlotUsageTracker
{
    private static readonly ConcurrentDictionary<int, DateTime> _lastUse = new();

    public static void Record(int slotId) => _lastUse[slotId] = DateTime.Now;

    public static DateTime? LastUse(int slotId) => _lastUse.TryGetValue(slotId, out var time) ? time : null;
}
