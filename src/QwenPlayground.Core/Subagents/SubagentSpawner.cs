using System.IO;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Subagents;

/// <summary>Запрос на спавн: задача (полная формулировка — субагент не видит контекст main'а) + заголовок окна.</summary>
public sealed record SubagentSpec(string Task, string? Title);

/// <summary>
/// Состояние текущего субагента для UI (кнопка в тулбаре, кнопка в tool call):
/// один субагент на всё приложение (синхронная модель). null в Spawner.Current —
/// субагента нет (окно закрыто или спавна не было в этой сессии приложения).
/// </summary>
public sealed record SubagentState(string SessionId, string Title, bool IsRunning);

/// <summary>
/// Итог спавна. Report — отчёт субагента (финальное сообщение; уходит main-агенту
/// как ответ инструмента). KvAnchored — был ли сохранён/восстановлен KV-якорь main'а.
/// </summary>
public sealed class SubagentOutcome
{
    public required string Report { get; init; }
    public bool KvAnchored { get; init; }
    /// <summary>Сообщение о деградации (нет места под якорь, save упал) — тул доносит до модели.</summary>
    public string? KvNote { get; init; }
}

/// <summary>
/// Оркестратор синхронного спавна субагента (решение 2026-09-22):
///   1. KV-якорь: save слота main (SlotAllocation.Main) в sessions/&lt;id&gt;-kv.bin
///      — если KvCacheEnabled и хватает места на диске;
///   2. исполнитель (Runner) — регистрирует UI (App): окно субагента, ход, отчёт;
///   3. finally: restore слота main из якоря + удаление файла (удалось restore).
///
/// Почему якорь обязателен: пул unified KV ≈ одна полная сессия — субагент, стартовав,
/// вытесняет KV main'а с GPU; при пиннинге сервер не восстановит его из RAM-кеша
/// (update_cache=false для явного id_slot). Без restore main'а следующий ход = полный
/// пере-евалюйт (десятки тысяч токенов).
///
/// Деградация мягкая и единая: нет места / save упал (501: сервер без --slot-save-path,
/// слот с медиа) → субагент запускается без якоря, KvNote объясняет причину.
/// Крах посреди субагента: файл якоря остаётся на диске (перезапись — на следующем
/// спавне) — основа будущего крах-восстановления.
/// </summary>
public sealed class SubagentSpawner
{
    /// <summary>Оценка байт KV на токен (f16 в файле; замер 2026-09-22: 188 МБ / 617 токенов).</summary>
    private const long BytesPerToken = 320_000;

    /// <summary>Запас свободного места сверх оценки снапшота (ГБ).</summary>
    private const long FreeMarginBytes = 2L * 1024 * 1024 * 1024;

    private readonly KvCacheController _kv;
    private readonly Func<string> _mainSessionId;

    public SubagentSpawner(KvCacheController kv, Func<string> mainSessionId)
    {
        _kv = kv;
        _mainSessionId = mainSessionId;
    }

    /// <summary>
    /// Исполнитель спавна: создаёт окно субагента (pinned-рантайм, профиль «subagent»,
    /// слот SlotAllocation.Subagent), проводит синхронный ход, возвращает отчёт.
    /// Регистрирует UI (App); null — субагенты недоступны (Harness/тесты).
    /// </summary>
    public Func<SubagentSpec, CancellationToken, Task<string>>? Runner { get; set; }

    // ── Состояние для UI (кнопка «Субагент»): обновляется App-исполнителем ──────────

    /// <summary>Текущий субагент (окно живо); null — нет. Один на всё приложение.</summary>
    public SubagentState? Current { get; private set; }

    /// <summary>Смена состояния (UI-потоке — без локов; подписчики — биндинги/флаги VM).</summary>
    public event Action? CurrentChanged;

    /// <summary>Окно субагента создано (App-исполнитель, после CreateSubagent).</summary>
    public void SetCurrent(string sessionId, string title, bool isRunning)
    {
        Current = new SubagentState(sessionId, title, isRunning);
        CurrentChanged?.Invoke();
    }

    /// <summary>Ход субагента стартовал/завершился (App-исполнитель, хук генерации).</summary>
    public void SetRunning(bool isRunning)
    {
        if (Current is { } state)
        {
            Current = state with { IsRunning = isRunning };
            CurrentChanged?.Invoke();
        }
    }

    /// <summary>Окно субагента закрыто (App, Closing) — кнопка исчезает.</summary>
    public void ClearCurrent(string sessionId)
    {
        if (Current is { } state && state.SessionId == sessionId)
        {
            Current = null;
            CurrentChanged?.Invoke();
        }
    }

    /// <summary>Путь якоря KV сессии: sessions/&lt;session-id&gt;-kv.bin (плоское имя — сервер не принимает разделители).</summary>
    public static string AnchorPath(string sessionId) =>
        Path.Combine(ChatSessions.Root, $"{sessionId}-kv.bin");

    /// <param name="callerSlot">Слот ВЫЗЫВАЮЩЕГО (main → 0, побочное окно → 2): именно его KV
    /// вытеснит субагент и именно его якорим. null — main (старые точки входа).</param>
    public async Task<SubagentOutcome> SpawnAsync(string task, string? title, int? callerSlot, CancellationToken cancellationToken)
    {
        if (Runner is null)
        {
            return new SubagentOutcome {
                Report = "Ошибка: субагент недоступен (UI-исполнитель не зарегистрирован).",
                KvNote = null
            };
        }

        // Один субагент на всё приложение (синхронная модель, слот 1 единственный):
        // без гварда побочное окно могло бы спавнить второго, пока первый работает —
        // два хода на одном слоте.
        if (Current is { } existing)
        {
            return new SubagentOutcome {
                Report = $"Ошибка: субагент уже существует ({(existing.IsRunning ? "работает" : "завершён")}: {existing.Title}). " +
                         "Дождитесь его завершения или закройте его окно; повторный спавн не поддерживается.",
                KvNote = null
            };
        }

        var caller = callerSlot ?? SlotAllocation.Main;
        var settings = AppSettings.Get();
        var anchor = AnchorPath(_mainSessionId());
        var anchored = false;
        string? kvNote = null;

        if (settings.KvCacheEnabled)
        {
            if (!await EnsureFreeSpaceAsync(anchor, caller, cancellationToken))
            {
                kvNote = "KV-якорь не сохранён: мало места на диске. Если субагент уйдёт в длину, возврат вызывающего агента будет стоить пере-евалюации его контекста.";
            }
            else if (await _kv.SaveSlotAsync(caller, Path.GetFileName(anchor), cancellationToken))
            {
                anchored = true;
            }
            else
            {
                kvNote = "KV-якорь не сохранён (save слота не удался: сервер без --slot-save-path, медиа в слоте или сетевая ошибка).";
            }
        }

        try
        {
            var report = await Runner(new SubagentSpec(task, title), cancellationToken);
            return new SubagentOutcome { Report = report, KvAnchored = anchored, KvNote = kvNote };
        }
        finally
        {
            if (anchored)
            {
                var restored = await _kv.RestoreSlotAsync(caller, Path.GetFileName(anchor), CancellationToken.None);
                if (restored)
                {
                    TryDelete(anchor);
                    DiagnosticsLog.Log($"Subagent: KV-якорь восстановлен (слот {caller}), файл удалён.");
                }
                else
                {
                    // Файл оставляем: основа крах-восстановления + ручное восстановление.
                    DiagnosticsLog.Log("Subagent: RESTORE KV-якоря НЕ удался — файл сохранён для ручного восстановления.");
                }
            }
        }
    }

    /// <summary>
    /// Хватит ли места на снапшот: n_prompt_tokens слота вызывающего × BytesPerToken + запас.
    /// Не удалось узнать размер (сервер молчит) — пускаем (better than never) с fallback-оценкой.
    /// </summary>
    private async Task<bool> EnsureFreeSpaceAsync(string anchor, int callerSlot, CancellationToken cancellationToken)
    {
        long estimated;
        var slots = await _kv.GetSlotsAsync(cancellationToken);
        var callerTokens = slots.FirstOrDefault(s => s.Id == callerSlot)?.NPromptTokens;
        estimated = callerTokens is { } tokens
            ? (long)tokens * BytesPerToken
            : 2L * 1024 * 1024 * 1024; // fallback: 2 ГБ

        var root = Path.GetPathRoot(anchor);
        if (root is null)
        {
            return true;
        }
        try
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < estimated + FreeMarginBytes)
            {
                DiagnosticsLog.Log(
                    $"Subagent: KV-якорь пропущен: нужно ~{(estimated + FreeMarginBytes) / 1e9:F1} ГБ, свободно {drive.AvailableFreeSpace / 1e9:F1} ГБ.");
                return false;
            }
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Log($"Subagent: DriveInfo не ответил ({exception.Message}) — якорь без проверки места.");
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Log($"Subagent: не удалось удалить якорь {path}: {exception.Message}");
        }
    }
}
