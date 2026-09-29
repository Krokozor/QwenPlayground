using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// «Стеклянная коробка»: показывает состояние чата (FSM), бюджет контекста,
/// KV-слоты сервера, последние сборки и память. Для владельца, который хочет понимать,
/// что происходит.
/// </summary>
public partial class DiagnosticsViewModel : ObservableObject
{
    private readonly ChatStateMachine _chatState;
    private readonly Func<int> _contextUsedTokensProvider;
    private readonly Func<int> _contextSizeProvider;
    private readonly Func<int> _maxTokensProvider;
    private readonly KvCacheController _kv;
    private readonly Func<int?> _currentSlotId;
    private readonly QwenPlayground.Core.Memory.MemoryStore _memory;
    private CancellationTokenSource? _slotsCts;

    [ObservableProperty]
    private string _chatStateName = "Idle";

    [ObservableProperty]
    private string _chatStateDescription = "Чат свободен.";

    [ObservableProperty]
    private int _contextUsedTokens;

    [ObservableProperty]
    private int _contextSize;

    [ObservableProperty]
    private int _contextUsagePercent;

    [ObservableProperty]
    private int _compactionThreshold;

    [ObservableProperty]
    private ObservableCollection<BuildInfo> _recentBuilds = new();

    [ObservableProperty]
    private int _memoryCount;

    [ObservableProperty]
    private ObservableCollection<MemoryInfo> _recentMemories = new();

    [ObservableProperty]
    private ObservableCollection<CrashEntryInfo> _crashEntries = new();

    [ObservableProperty]
    private CrashEntryInfo? _selectedCrash;

    [ObservableProperty]
    private string _selectedCrashText = "Выберите запись, чтобы увидеть детали.";

    // ── KV-слоты сервера ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<SlotRowInfo> _slotRows = new();

    [ObservableProperty]
    private string _slotStatus = "Слоты не запрошены.";

    [ObservableProperty]
    private bool _slotsBusy;

    // ── Метрики сервера (/metrics, флаг --metrics) ──────────────────────────────────
    // Скорости считаются ИЗ СЧЁТЧИКОВ (дельты за интервал поллинга), а не из gauge'ей
    // сервера: llamacpp:prompt_tokens_seconds — среднее за ВСЮ жизнь сервера, оно ползёт
    // медленно и не покажет «вот сейчас упало». Счётчики кумулятивные → дельта/время.

    private static readonly TimeSpan MetricsPollInterval = TimeSpan.FromSeconds(5);
    private readonly System.Net.Http.HttpClient _metricsHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
    private long? _prevPromptTokens;
    private long? _prevPredictedTokens;
    private DateTime _prevSampleUtc = DateTime.MinValue;
    private readonly Queue<double> _promptTpsWindow = new(); // 60 × 5 с = 5 минут
    private const int TpsWindowMax = 60;

    [ObservableProperty]
    private string _promptTps = "—";

    [ObservableProperty]
    private string _predictedTps = "—";

    [ObservableProperty]
    private string _tpsWindow = "";

    [ObservableProperty]
    private string _maxPromptTokens = "—";

    [ObservableProperty]
    private string _metricsStatus = "метрики не опрашиваются (нет --metrics?)";

    public DiagnosticsViewModel(
        ChatStateMachine chatState,
        Func<int> contextUsedTokensProvider,
        Func<int> contextSizeProvider,
        Func<int> maxTokensProvider,
        KvCacheController kv,
        Func<int?> currentSlotId,
        QwenPlayground.Core.Memory.MemoryStore memory)
    {
        _chatState = chatState;
        _contextUsedTokensProvider = contextUsedTokensProvider;
        _contextSizeProvider = contextSizeProvider;
        _maxTokensProvider = maxTokensProvider;
        _kv = kv;
        _currentSlotId = currentSlotId;
        _memory = memory;

        _chatState.StateChanged += (_, to) => UpdateChatState(to);
        UpdateChatState(_chatState.Current);
        Refresh();
        _ = MetricsLoopAsync();
    }

    /// <summary>
    /// Опрос /metrics раз в 5 с: дельты счётчиков → текущие т/с + окно 5 минут.
    /// Ошибки (сервер лёг, нет --metrics) — в статус, цикл живёт дальше.
    /// </summary>
    private async Task MetricsLoopAsync()
    {
        while (true)
        {
            await Task.Delay(MetricsPollInterval);
            var endpoint = QwenPlayground.Core.Settings.AppSettings.Get().Endpoint;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                MetricsStatus = "адрес сервера не задан";
                continue;
            }
            try
            {
                var body = await _metricsHttp.GetStringAsync(endpoint.TrimEnd('/') + "/metrics");
                var now = DateTime.UtcNow;
                var (promptTotal, predictedTotal, maxTokens) = ParseMetrics(body);
                if (promptTotal is null)
                {
                    MetricsStatus = "сервер не отвечает на /metrics (перезапустите с --metrics)";
                    continue;
                }
                // Дельты за интервал; отрицательная дельта — счётчики сбросились (рестарт сервера).
                if (_prevPromptTokens is { } prevPrompt && _prevPredictedTokens is { } prevPredicted &&
                    now > _prevSampleUtc)
                {
                    var dt = (now - _prevSampleUtc).TotalSeconds;
                    if (promptTotal.Value >= prevPrompt && predictedTotal is { } pTotal && pTotal >= prevPredicted && dt > 0)
                    {
                        var promptRate = (promptTotal.Value - prevPrompt) / dt;
                        var predictedRate = (pTotal - prevPredicted) / dt;
                        PromptTps = promptRate > 0.5 ? $"{promptRate:F0} т/с" : "—";
                        PredictedTps = predictedRate > 0.5 ? $"{predictedRate:F1} т/с" : "—";
                        if (promptRate > 0.5)
                        {
                            _promptTpsWindow.Enqueue(promptRate);
                            while (_promptTpsWindow.Count > TpsWindowMax)
                            {
                                _promptTpsWindow.Dequeue();
                            }
                            TpsWindow = $"prompt т/с за {TpsWindowMax * 5 / 60} мин: {_promptTpsWindow.Min():F0}–{_promptTpsWindow.Max():F0} (последний интервал)";
                        }
                    }
                }
                _prevPromptTokens = promptTotal;
                _prevPredictedTokens = predictedTotal;
                _prevSampleUtc = now;
                MaxPromptTokens = maxTokens is { } mt ? $"{mt:N0} ток." : "—";
                MetricsStatus = "ок (поллинг 5 с)";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                MetricsStatus = $"ошибка опроса: {ex.Message}";
            }
        }
    }

    /// <summary>Парсинг Prometheus-текста: счётчики prompt/predicted-токенов + максимум промпта.</summary>
    private static (long? PromptTotal, long? PredictedTotal, long? MaxTokens) ParseMetrics(string body)
    {
        long? prompt = null, predicted = null, maxTokens = null;
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }
            var parts = line.Split(' ', 2);
            if (parts.Length != 2 || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }
            switch (parts[0])
            {
                case "llamacpp:prompt_tokens_total": prompt = (long)value; break;
                case "llamacpp:tokens_predicted_total": predicted = (long)value; break;
                case "llamacpp:n_tokens_max": maxTokens = (long)value; break;
            }
        }
        return (prompt, predicted, maxTokens);
    }

    private void UpdateChatState(ChatState state)
    {
        ChatStateName = state.ToString();
        ChatStateDescription = state switch
        {
            ChatState.Idle => "Чат свободен, можно отправлять сообщения.",
            ChatState.Generating => "Агент работает: генерация или выполнение инструментов.",
            ChatState.Compacting => "Идёт сжатие контекста (ручное или автоматическое).",
            ChatState.AwaitingConfirmation => "Ожидает подтверждения действия (confirm).",
            ChatState.RestartPending => "Запрошен перезапуск в новую версию.",
            _ => state.ToString()
        };
        // Свежий серверный счётчик контекста (ContextUsedTokens/ContextSize) после каждого
        // перехода FSM, в т.ч. по завершении генерации — не только по кнопке «Обновить».
        Refresh();
    }

    /// <summary>
    /// Запросить /slots и перестроить строки. Конкурентные вызовы (FSM-переходы) отменяют
    /// предыдущий запрос — в UI попадает только самый свежий снимок.
    /// </summary>
    private async Task RefreshSlotsAsync()
    {
        _slotsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _slotsCts = cts;
        SlotsBusy = true;
        try
        {
            var slots = await _kv.GetSlotsAsync(cts.Token);
            if (cts.Token.IsCancellationRequested)
            {
                return;
            }
            var current = _currentSlotId();
            var rows = new ObservableCollection<SlotRowInfo>();
            var totalTokens = 0;
            var totalCtx = 0;
            foreach (var slot in slots.OrderBy(s => s.Id))
            {
                if (slot.NPromptTokens is { } tokens)
                {
                    totalTokens += tokens;
                }
                if (slot.NCtx is { } ctx)
                {
                    totalCtx += ctx;
                }
                var hasKv = slot.NPromptTokens is { } kt && kt > 0;
                rows.Add(new SlotRowInfo(
                    slot.Id,
                    SlotRole(slot.Id),
                    slot.NPromptTokens is { } rowTokens ? $"{rowTokens:N0} ток." : "—",
                    slot.NCacheTokens is { } cache && cache > 0 ? $"кэш: {cache:N0}" : "кэш: 0",
                    LastUseDisplay(slot.Id, hasKv),
                    IsStale(slot.Id, hasKv),
                    slot.IsProcessing,
                    slot.Id == current));
            }
            SlotRows = rows;
            SlotStatus = slots.Count == 0
                ? "сервер не ответил (не запущен?)"
                : $"слотов: {slots.Count} (нужно 5: 0 main, 1-2 окна, 3 субагент, 4 пробы) · " +
                  $"пул занят: {totalTokens:N0}" + (totalCtx > 0 ? $" / {totalCtx:N0} ток. ({totalTokens * 100 / totalCtx}%)" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            // новый запрос пришёл — этот снимок не актуален
        }
        catch (Exception ex)
        {
            if (!cts.Token.IsCancellationRequested)
            {
                SlotStatus = $"не удалось запросить слоты: {ex.Message}";
            }
        }
        finally
        {
            if (!cts.Token.IsCancellationRequested)
            {
                SlotsBusy = false;
            }
        }
    }

    /// <summary>Подпись роли слота по схеме SlotAllocation (0 main, 1-2 окна, 3 субагент, 4 пробы).</summary>
    private static string SlotRole(int id) => id switch
    {
        SlotAllocation.Main => "main",
        SlotAllocation.NonMain => "окно (не-main)",
        SlotAllocation.SideWindow => "окно (разводка)",
        SlotAllocation.Subagent => "субагент",
        SlotAllocation.Probe => "пробы",
        _ => "—"
    };

    /// <summary>
    /// Когда слот последний раз использовался ходом/пробой (SlotUsageTracker) + метка «застыл»:
    /// KV есть, а ходы не трогают дольше часа — мусор, кандидат на чистку. Неизвестно
    /// (приложение не использовало слот с этого старта) — «?», метку не ставим.
    /// </summary>
    private static string LastUseDisplay(int slotId, bool hasKv)
    {
        var last = SlotUsageTracker.LastUse(slotId);
        if (last is not { } use)
        {
            return hasKv ? "застыл? (не я)" : "—";
        }
        var age = DateTime.Now - use.Time;
        var display = age.TotalHours >= 24
            ? $"{(int)age.TotalHours} ч"
            : age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes} мин" : "сейчас";
        // Владелец (фаза 3, план 2026-09-28): ЧЕЙ это KV — id сессии или «пробы».
        var owner = use.Owner == "пробы" ? "пробы" : $"сессия {use.Owner[..Math.Min(8, use.Owner.Length)]}";
        return (IsStale(slotId, hasKv) ? $"застыл {display}" : display) + $" ({owner})";
    }

    /// <summary>«Застыл»: KV есть, а приложение слотом не пользовалось больше часа (кандидат на чистку).</summary>
    private static bool IsStale(int slotId, bool hasKv) =>
        hasKv && SlotUsageTracker.LastUse(slotId) is { } use && (DateTime.Now - use.Time).TotalHours >= 1;

    /// <summary>Вычистить KV одного слота (кнопка в строке).</summary>
    [RelayCommand]
    private async Task EraseSlotAsync(SlotRowInfo? row)
    {
        if (row is null)
        {
            return;
        }
        var ok = await _kv.EraseSlotAsync(row.Id);
        SlotStatus = ok
            ? $"слот {row.Id} ({SlotRole(row.Id)}) вычищен"
            : $"не удалось вычистить слот {row.Id} (сервер без --slot-save-path? слот с медиа?)";
        await RefreshSlotsAsync();
    }

    /// <summary>
    /// Вычистить ВСЕ слоты с KV (кнопка «вычистить все»): освобождает единый KV-пул,
    /// промпт-обработка всех чатов ускоряется. Подтверждение — необратимо для живых кэшей.
    /// </summary>
    [RelayCommand]
    private async Task EraseAllSlotsAsync()
    {
        var confirm = new Views.ConfirmWindow("Вычистить KV всех слотов? Живые кэши чатов потеряются — следующие ходы пере-евалюируют промпты.")
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (confirm.ShowDialog() != true)
        {
            return;
        }
        var slots = await _kv.GetSlotsAsync();
        var erased = 0;
        foreach (var slot in slots.OrderBy(s => s.Id))
        {
            if (await _kv.EraseSlotAsync(slot.Id))
            {
                erased++;
            }
        }
        SlotStatus = $"вычищено слотов: {erased} из {slots.Count}";
        await RefreshSlotsAsync();
    }

    [RelayCommand]
    private void Refresh()
    {
        // KV-слоты: асинхронно (сеть), снимок самый свежий (старый запрос отменяется).
        _ = RefreshSlotsAsync();

        // Бюджет контекста
        ContextUsedTokens = _contextUsedTokensProvider();
        ContextSize = _contextSizeProvider();
        var maxTokens = _maxTokensProvider();
        CompactionThreshold = Math.Max(0, ContextSize - maxTokens - ContextCompactor.CompactionReserveTokens);
        ContextUsagePercent = ContextSize > 0
            ? Math.Min(100, ContextUsedTokens * 100 / ContextSize)
            : 0;

        // Последние сборки
        var journal = BuildJournal.Load(SelfBuildPaths.RunRoot);
        RecentBuilds = new ObservableCollection<BuildInfo>(
            journal.OrderByDescending(b => b.Timestamp).Take(10)
                .Select(b => new BuildInfo(b.Id, b.Timestamp, b.Status, b.FailureReason, b.BuildOutputTail)));

        // Память (общий store Main — фаза 3, план 2026-09-28, guard-тест поймал new здесь)
        var memories = _memory.List();
        MemoryCount = memories.Count;
        RecentMemories = new ObservableCollection<MemoryInfo>(
            memories.Take(5).Select(m => new MemoryInfo(m.Id, MemoryClassifier.TopName(m.CategoryLayers), m.CreatedAt, m.Content)));

        // Крахи: оба канала (приложение + лаунчер), новые сверху.
        var logsDir = CrashLog.LogsDir;
        var entries = new List<CrashEntryInfo>();
        foreach (var channel in new[] { CrashLogCore.AppChannel, CrashLogCore.LauncherChannel })
        {
            foreach (var text in CrashLogCore.ReadEntries(logsDir, channel, max: 20))
            {
                entries.Add(ParseCrashEntry(channel, text));
            }
        }
        CrashEntries = new ObservableCollection<CrashEntryInfo>(
            entries.OrderByDescending(e => e.Time).Take(20));
        SelectedCrash = CrashEntries.Count > 0 ? CrashEntries[0] : null;
        SelectedCrashText = SelectedCrash?.FullText ?? "Записей нет — крахов не было (или лог пуст).";
    }

    private static CrashEntryInfo ParseCrashEntry(string channel, string text)
    {
        string time = "?", source = "?", process = "?";
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("Time: ", StringComparison.Ordinal))
            {
                time = line["Time: ".Length..].Trim();
            }
            else if (line.StartsWith("Source: ", StringComparison.Ordinal))
            {
                source = line["Source: ".Length..].Trim();
            }
            else if (line.StartsWith("Process: ", StringComparison.Ordinal))
            {
                process = line["Process: ".Length..].Trim();
            }
        }
        return new CrashEntryInfo(channel, time, source, process, text);
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        // Папка логов: полный разбор (дневные файлы, watchdog.log, launcher.log).
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{CrashLog.LogsDir}\"",
            UseShellExecute = true
        });
    }
}

/// <summary>
/// Строка KV-слота в «Диагностике»: id, роль (схема SlotAllocation), занятость, кэш
/// (переиспользованный префикс последнего запроса), последнее использование (+ «застыл»),
/// обработка, «это слот текущей сессии».
/// </summary>
public sealed record SlotRowInfo(int Id, string Role, string Tokens, string Cache, string LastUse, bool Stale, bool Processing, bool IsCurrentSession);

public sealed record BuildInfo(string Id, DateTime Timestamp, string Status, string? FailureReason, string OutputTail);

public sealed record MemoryInfo(string Id, string Category, DateTime CreatedAt, string Content);

public sealed record CrashEntryInfo(string Channel, string Time, string Source, string Process, string FullText);
