using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Runtime;

/// <summary>
/// Скоуп агента — изолированный контекст исполнения хода (шаг к оркестратору,
/// дизайн в ARCHITECTURE.md «Будущее: параллельные ходы»).
///
/// Что изолировано СЕЙЧАС: маршрут интерактива (per-scope Confirm — карточка
/// подтверждения в чате собственного рантайма, фаза 2). Настройки НЕ изолированы:
/// <see cref="SettingsProvider"/> по умолчанию — процессный синглтон, а per-чат
/// переопределения (семплер/промпт/state-блок) живут в ChatProfiles по ключам
/// сессии (их резолвит TurnPipeline). Провайдер — крючок на будущий per-scope
/// профиль настроек (оркестратор: свой Endpoint), а не готовая изоляция.
///
/// <see cref="Main"/> — скоуп главного агента: интерактив регистрирует UI
/// (через фасад <see cref="Tools.AgentInteraction"/>).
///
/// Потоковая модель (главный инвариант проекта): весь агентный код исполняется
/// на потоке UI — мутации делегатов маршрута без локов корректны.
/// </summary>
public sealed class AgentRuntime
{
    /// <summary>Скоуп main-агента; дефолт для всех существующих точек входа.</summary>
    public static AgentRuntime Main { get; } = new();

    /// <summary>
    /// Настройки на ход: снапшот процессного синглтона в момент старта хода (цикл
    /// фиксирует значения локально — смена в UI действует со следующего хода).
    /// Func, а не значение: синглтон может перечитываться (<c>Reload</c>), а ход
    /// должен видеть живой источник правды в момент старта. Per-чат переопределения
    /// НЕ здесь — в ChatProfiles (ключи сессии); per-scope профиль настроек
    /// не реализован, это провайдер — крючок на него.
    /// </summary>
    public Func<AppSettings> SettingsProvider { get; init; } = () => AppSettings.Get();

    /// <summary>Настройки этого скоупа на момент обращения (через провайдер).</summary>
    public AppSettings Settings => SettingsProvider();

    /// <summary>Маршрут интерактива: подтверждение опасного действия (null — недоступен).</summary>
    public Func<string, CancellationToken, Task<bool>>? Confirm { get; set; }

    /// <summary>Подтверждение через зарегистрированного провайдера; null — интерактив недоступен.</summary>
    public Task<bool>? TryConfirm(string question, CancellationToken cancellationToken) =>
        Confirm is { } confirm ? confirm(question, cancellationToken) : null;
}
