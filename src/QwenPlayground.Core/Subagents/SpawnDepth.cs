namespace QwenPlayground.Core.Subagents;

/// <summary>
/// P5 (внешнее ревью 2026-10-01): глубина спавна — дешёвая страховка от рекурсии субагентов.
/// 0 — main-агент/побочное окно, 1 — ход субагента. AsyncLocal течёт через цепочку await
/// (ход субагента исполняется внутри <c>await Runner(...)</c> в <see cref="SubagentSpawner.SpawnAsync"/>) —
/// на едином UI-потоке инструменты субагента видят глубину 1, а вызывающего — 0.
///
/// Основной отказ — хард-блэклист скоупа субагента в ToolRegistry (диспетчер, до исполнения);
/// это вторая линия (defense in depth): если профиль/диспетчер когда-нибудь изменят,
/// субагент всё равно не сможет спавнить субагента.
/// </summary>
public static class SpawnDepth
{
    private static readonly AsyncLocal<int?> _depth = new();

    /// <summary>Текущая глубина: 0 — main/окно, 1 — ход субагента (2+ — недостижимо: на 1 уже отказ).</summary>
    public static int Current => _depth.Value ?? 0;

    /// <summary>Исполнить ход субагента на глубине +1 (предыдущее значение восстанавливается в finally).</summary>
    public static async Task<T> RunAsSubagentAsync<T>(Func<Task<T>> body)
    {
        var previous = _depth.Value;
        _depth.Value = Current + 1;
        try
        {
            return await body();
        }
        finally
        {
            _depth.Value = previous;
        }
    }
}
