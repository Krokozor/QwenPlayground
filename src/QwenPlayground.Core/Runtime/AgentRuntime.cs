using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Runtime;

/// <summary>
/// Скоуп агента — изолированный контекст исполнения хода (шаг к оркестратору,
/// дизайн в ARCHITECTURE.md «Будущее: параллельные ходы»). Процессная статика
/// (настройки, маршрут интерактива, каталог сессий) переезжает сюда постепенно:
/// новые подсистемы принимают скоуп параметром, старые мигрируют «по касанию».
///
/// <see cref="Main"/> — скоуп главного агента: провайдер настроек указывает на
/// процессный синглон <see cref="AppSettings.Get()"/>, интерактив регистрирует UI
/// (через фасад <see cref="Tools.AgentInteraction"/>). Поведение существующего кода
/// не меняется, но дочерний агент сможет получить собственный профиль настроек
/// и собственный маршрут интерактива, не трогая цикл.
///
/// Потоковая модель (главный инвариант проекта): весь агентный код исполняется
/// на потоке UI — мутации делегатов маршрута без локов корректны.
/// </summary>
public sealed class AgentRuntime
{
    /// <summary>Скоуп main-агента; дефолт для всех существующих точек входа.</summary>
    public static AgentRuntime Main { get; } = new();

    /// <summary>
    /// Провайдер профиля настроек. По умолчанию — процессный синглтон; изолированный
    /// агент отдаёт собственный экземпляр (в т.ч. свой Endpoint/семплер/лимиты).
    /// Func, а не значение: синглтон может перечитываться (<c>Reload</c>), а ход
    /// должен видеть живой источник правды в момент старта.
    /// </summary>
    public Func<AppSettings> SettingsProvider { get; init; } = () => AppSettings.Get();

    /// <summary>Профиль настроек этого скоупа на момент обращения.</summary>
    public AppSettings Settings => SettingsProvider();

    /// <summary>Маршрут интерактива: подтверждение опасного действия (null — недоступен).</summary>
    public Func<string, CancellationToken, Task<bool>>? Confirm { get; set; }

    /// <summary>Подтверждение через зарегистрированного провайдера; null — интерактив недоступен.</summary>
    public Task<bool>? TryConfirm(string question, CancellationToken cancellationToken) =>
        Confirm is { } confirm ? confirm(question, cancellationToken) : null;
}
