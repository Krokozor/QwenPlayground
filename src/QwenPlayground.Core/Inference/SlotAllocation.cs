namespace QwenPlayground.Core.Inference;

/// <summary>
/// Детерминированная схема слотов llama.cpp (решение по субагентам, 2026-09-22):
/// каждый класс запросов привязан к своему слоту (id_slot в /completion), чтобы
/// KV-менеджмент (save/restore/erase, --no-cache-idle-slots) знал, ЧЕЙ слот есть
/// чей, без гадания по /slots.
///
/// Почему пиннинг обязателен: при явном id_slot сервер НЕ ходит в RAM prompt cache
/// (b10353: update_cache=false для явного слота) — пиннутый слот живёт в GPU
/// (--no-cache-idle-slots) и защищается только файловым якорем (KvCacheController).
///
/// Бюджет: 4 слота. Слот 2 пока используют только ручные «окна чата» (＋ Окно чата);
/// второй параллельный субагент — позже (аллокатор вместо фиксированной схемы).
/// </summary>
public static class SlotAllocation
{
    /// <summary>Main-агент (главное окно, любая текущая сессия главного окна).</summary>
    public const int Main = 0;

    /// <summary>Субагент (spawn_subagent): один на всё приложение (синхронная модель).</summary>
    public const int Subagent = 1;

    /// <summary>Ручные побочные окна чата (＋ Окно чата).</summary>
    public const int SideWindow = 2;

    /// <summary>Сервисные LLM-вызовы (суммаризация/компакция/память): свой слот, чтобы
    /// LRU-выбор сервисного запроса никогда не топил KV main/субагента.</summary>
    public const int Service = 3;
}
