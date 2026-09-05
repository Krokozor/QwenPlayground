using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Crash;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// «Стеклянная коробка»: показывает состояние чата (FSM), бюджет контекста,
/// последние сборки и память. Для владельца, который хочет понимать, что происходит.
/// </summary>
public partial class DiagnosticsViewModel : ObservableObject
{
    private readonly ChatStateMachine _chatState;
    private readonly Func<int> _contextUsedTokensProvider;
    private readonly Func<int> _contextSizeProvider;
    private readonly Func<int> _maxTokensProvider;

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

    public DiagnosticsViewModel(
        ChatStateMachine chatState,
        Func<int> contextUsedTokensProvider,
        Func<int> contextSizeProvider,
        Func<int> maxTokensProvider)
    {
        _chatState = chatState;
        _contextUsedTokensProvider = contextUsedTokensProvider;
        _contextSizeProvider = contextSizeProvider;
        _maxTokensProvider = maxTokensProvider;

        _chatState.StateChanged += (_, to) => UpdateChatState(to);
        UpdateChatState(_chatState.Current);
        Refresh();
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

    [RelayCommand]
    private void Refresh()
    {
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

        // Память
        var store = new MemoryStore();
        var memories = store.List();
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

public sealed record BuildInfo(string Id, DateTime Timestamp, string Status, string? FailureReason, string OutputTail);

public sealed record MemoryInfo(string Id, string Category, DateTime CreatedAt, string Content);

public sealed record CrashEntryInfo(string Channel, string Time, string Source, string Process, string FullText);
