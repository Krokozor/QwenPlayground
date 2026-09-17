using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Crash;

/// <summary>
/// Детальный лог диагностики: синхронный построчный трейс в logs/diag-YYYYMMDD.log.
/// Пишет только когда включён режим диагностики (AppSettings.DiagnosticsMode) —
/// в нормальном режиме вызов дёшев (один bool-чек) и на диск ничего не идёт.
///
/// Зачем: «приложение запустилось, сделало хендшейк, а потом повисло». В минимальном
/// режиме последний след — «UI alive (+N s)». В детальном режиме видно, на чём именно
/// завис: итерация цикла, LLM-запрос, tool call, MCP-вызов, проба, стадия компакции.
///
/// Формат строки как у StartupTrace: «секунды с запуска процесса, wall-clock,
/// номер потока, сообщение». Синхронная запись, исключения глотаются: логгер не
/// должен ломать тот самый код, который диагностируем.
///
/// Переключение извне (без работающего приложения): правка settings.json
/// (DiagnosticsMode: true) + перезапуск. Из живого приложения: set_setting.
/// </summary>
public static class DiagnosticsLog
{
    private static readonly object Lock = new();
    private static readonly DateTime ProcessStart = DateTime.Now;
    private static bool _enabled;
    private static bool _checked;

    /// <summary>Каталог логов для тестов (null — дефолтный logs/ в корне workspace).</summary>
    private static string? _logsDirOverride;

    /// <summary>
    /// Текущий уровень. Читается лениво: первый вызов Log определяет режим,
    /// далее переопределяется только через SetEnabled (тесты / смена на лету).
    /// </summary>
    public static bool Enabled
    {
        get
        {
            if (!_checked)
            {
                _checked = true;
                try
                {
                    _enabled = AppSettings.Get().DiagnosticsMode;
                }
                catch
                {
                    // Настройки недоступны (ранний старт / битый файл) — режим выключен.
                    _enabled = false;
                }
            }
            return _enabled;
        }
    }

    /// <summary>Принудительно установить режим (обходит ленивое чтение настроек).</summary>
    public static void SetEnabled(bool enabled)
    {
        _checked = true;
        _enabled = enabled;
    }

    /// <summary>Только для тестов: каталог логов (null — дефолт).</summary>
    public static void SetLogsDirForTests(string? logsDir)
    {
        lock (Lock)
        {
            _logsDirOverride = logsDir;
        }
    }

    /// <summary>Только для тестов: сбросить ленивое чтение и override.</summary>
    public static void ResetForTests()
    {
        lock (Lock)
        {
            _checked = false;
            _enabled = false;
            _logsDirOverride = null;
        }
    }

    /// <summary>Файл дневного diag-лога (создаётся при первой записи).</summary>
    public static string DailyFile(string logsDir) =>
        Path.Combine(logsDir, $"diag-{DateTime.Now:yyyyMMdd}.log");

    private static string LogsDir()
    {
        lock (Lock)
        {
            if (_logsDirOverride is { } overrideDir)
            {
                return overrideDir;
            }
        }
        return Path.Combine(SelfBuild.SelfBuildPaths.WorkspaceRoot, "logs");
    }

    /// <summary>
    /// Записать строку в diag-лог, если режим включён. Никогда не бросает.
    /// </summary>
    public static void Log(string message)
    {
        if (!Enabled)
        {
            return;
        }
        try
        {
            var line = $"[{(DateTime.Now - ProcessStart).TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}s {DateTime.Now:HH:mm:ss.fff}] [T{Environment.CurrentManagedThreadId}] {message}";
            lock (Lock)
            {
                var logsDir = LogsDir();
                Directory.CreateDirectory(logsDir);
                File.AppendAllText(DailyFile(logsDir), line + Environment.NewLine);
            }
        }
        catch
        {
            // diag-лог не должен ломать диагностируемый код
        }
    }
}
