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

    /// <summary>
    /// Стартовая рабочая папка НОВОЙ сессии (2026-10-05): текущий глобальный дефолт
    /// (настройка ProjectRoot) как есть. Пустой/пробельный дефолт — не задано (null).
    /// Пустой root остаётся только у сессий СТАРЫХ версий чата (в файле поля нет) —
    /// они разрешаются фоллбеком через <see cref="Resolve"/>; новые сессии получают
    /// папку проекта явно, чтобы «рабочая папка» в UI и в файле сессии совпадала с тем,
    /// где инструменты реально работают.
    /// </summary>
    public static string? DefaultForNew(string? settingsRoot)
    {
        var root = settingsRoot?.Trim();
        return string.IsNullOrEmpty(root) ? null : root;
    }
}
