namespace QwenPlayground.Core.Sessions;

/// <summary>
/// Рабочая папка сессии (root инструментов, план 2026-10-01): разрешение.
/// Иерархия: своя папка сессии (SessionData.Root) → глобальная настройка
/// AppSettings.ProjectRoot (дефолт) → null (рабочей папки нет). Настройка —
/// провайдер значения по умолчанию, а не источник истины: источник — сессия.
/// </summary>
public static class SessionRoots
{
    /// <summary>
    /// Разрешённый root: своя у сессии, иначе глобальный дефолт; null — нигде.
    /// Пустые/пробельные значения считаются «не задано».
    /// </summary>
    public static string? Resolve(string? sessionRoot, string? settingsRoot)
    {
        if (!string.IsNullOrWhiteSpace(sessionRoot))
        {
            return sessionRoot.Trim();
        }
        if (!string.IsNullOrWhiteSpace(settingsRoot))
        {
            return settingsRoot.Trim();
        }
        return null;
    }
}
