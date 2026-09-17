using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Tools;

/// <summary>
/// Единая точка решения «рекламировать ли memory_*-тул». Мастер-переключатель
/// <see cref="AppSettings.MemoryEnabled"/> выключает «умную» память (реколл, слои, дедуп) и
/// скрывает соответствующие тулы. НО базовые тулы-«записки» (<see cref="IsBasicMemoryTool"/>)
/// работают ВСЕГДА: сохранение факта не зависит от компаньона и остаётся доступным, даже
/// когда память выключена. Используется в обеих точках рекламы (реальный запрос + превью/бюджет),
/// чтобы они оставались совпадающими.
/// </summary>
public static class MemoryToolGate
{
    public const string DisabledMessage =
        "Memory is disabled (Settings → Memory → Память: вкл/выкл). Enable it to use memory tools.";

    public static bool IsMemoryTool(string name) =>
        !string.IsNullOrEmpty(name) && name.StartsWith("memory_", StringComparison.Ordinal);

    /// <summary>
    /// Базовые тулы памяти: доступны независимо от мастер-переключателя. Это «записки» —
    /// фундаментальная операция записи факта, не требующая компаньона/реколла.
    /// </summary>
    public static bool IsBasicMemoryTool(string name) =>
        name == "memory_add";

    public static bool ShouldAdvertise(string name) =>
        !IsMemoryTool(name) || IsBasicMemoryTool(name) || AppSettings.Get().MemoryEnabled;
}
