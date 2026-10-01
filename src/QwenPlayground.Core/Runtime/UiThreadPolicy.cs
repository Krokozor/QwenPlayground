using QwenPlayground.Core.Crash;

namespace QwenPlayground.Core.Runtime;

/// <summary>
/// Инвариант проекта: всё агентное ядро (разговор, FSM, память, метки слотов) живёт
/// на UI-потоке — без локов, «по построению». Этот класс превращает «по построению»
/// в проверяемое утверждение: App шнурует <see cref="IsUiThread"/> в Dispatcher.CheckAccess(),
/// мутаторы ядра вызывают <see cref="Assert"/> — нарушение пишет контекст в always-on
/// events-лог и кидает исключение (degradation must be loud: тихий сломанный harness
/// хуже громкого краша).
///
/// Без #if DEBUG: приложение живёт в Release, и сам процесс — среда разработки
/// (решение владельца 2026-10-01). В тестах и в watchdog-процессе <see cref="IsUiThread"/>
/// не шнурован — Assert no-op (Core не знает WPF, шнуровка — ответственность App).
/// </summary>
public static class UiThreadPolicy
{
    /// <summary>
    /// Кто UI-поток (App: <c>() =&gt; Dispatcher.CheckAccess()</c>). null — ассерты выключены
    /// (тесты, watchdog, любой процесс без WPF).
    /// </summary>
    public static Func<bool>? IsUiThread { get; set; }

    /// <summary>
    /// true — бросать исключение при нарушении (дефолт). false — только events-лог
    /// (аварийный вентиль, если краш мешает диагностике).
    /// </summary>
    public static bool ThrowOnViolation { get; set; } = true;

    /// <summary>
    /// Утверждение «мы на UI-потоке» для точки мутации ядра.
    /// <paramref name="context"/> — короткая метка места (метод + что делает); попадает
    /// в events-лог и в сообщение исключения — это и есть данные постмортема.
    /// </summary>
    public static void Assert(string context)
    {
        var isUiThread = IsUiThread;
        if (isUiThread is null || isUiThread())
        {
            return;
        }
        var message = $"UI-thread invariant violated: {context} called from a background thread";
        AppEventLog.Log($"UiThread: {message}");
        if (ThrowOnViolation)
        {
            throw new InvalidOperationException(message);
        }
    }
}
