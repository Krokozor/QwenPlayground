using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Tools;

/// <summary>
/// Единая точка решения «рекламировать ли memory_*-тул»: мастер-переключатель
/// <see cref="AppSettings.MemoryEnabled"/>. Выкл — тулы памяти не попадают в промпт, модель не
/// может их вызвать. Используется в обеих точках рекламы (реальный запрос + превью/бюджет), чтобы
/// они оставались совпадающими.
/// </summary>
public static class MemoryToolGate
{
    public const string DisabledMessage =
        "Memory is disabled (Settings → Memory → Память: вкл/выкл). Enable it to use memory tools.";

    public static bool IsMemoryTool(string name) =>
        !string.IsNullOrEmpty(name) && name.StartsWith("memory_", StringComparison.Ordinal);

    public static bool ShouldAdvertise(string name) =>
        AppSettings.Get().MemoryEnabled || !IsMemoryTool(name);
}
