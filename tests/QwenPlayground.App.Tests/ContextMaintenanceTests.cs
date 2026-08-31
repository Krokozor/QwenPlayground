using System.IO;
using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Sessions;

namespace QwenPlayground.App.Tests;

/// <summary>
/// FSM-контракт и сценарии компакции на фейковом LLM (делегат completeStructured).
/// Всё изолировано: temp-каталог сессий, собственный layerStore, память — по требованию.
/// </summary>
public sealed class ContextMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qpw_maint_" + Guid.NewGuid().ToString("N"));
    private readonly ChatLog _log = new();
    private readonly ChatStateMachine _chat = new();
    private readonly CompactionPreview _preview = new();
    private readonly MemorySurfacer _surfacer = new();
    private readonly List<string> _statuses = [];
    private bool _generating;

    private ContextMaintenance Create(
        Func<string, string?, Action<string>?, CancellationToken, Task<string?>>? complete = null,
        string? sessionId = null,
        Func<CancellationToken, Task<int>>? countTokens = null)
    {
        return new ContextMaintenance(
            _log,
            _chat,
            _preview,
            complete ?? ((user, system, onChunk, ct) => Task.FromResult<string?>("резюме диалога")),
            new MemoryLayerStore(_root),
            _surfacer,
            countTokens ?? ((ct) => Task.FromResult(100)),
            () => Task.FromResult(32768),
            () => sessionId ?? "branch-session",
            new ContextBackupStore(_root, Path.Combine(_root, "backups")),
            new ContextMaintenance.Ui(
                s => _statuses.Add(s),
                g => _generating = g,
                SaveCurrent));
    }

    private void SaveCurrent()
    {
        // Владелец сохраняет сам; для тестов достаточно события.
    }

    private void Fill(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var role = i % 2 == 0 ? ChatRole.User : ChatRole.Assistant;
            _log.Add(new ChatMessage { Role = role, Content = $"сообщение номер {i} с достаточным объёмом текста для оценки токенов" });
        }
    }

    /// <summary>Бэкап требует файл на диске: в реальном ходе SaveCurrent всегда был раньше.</summary>
    private void Persist(string sessionId = "branch-session") =>
        new SessionStore(_root).Save(sessionId, _log.ToList());

    [Fact]
    public async Task EmptyLog_ReportsNothingToCompress_NoStateChange()
    {
        var maintenance = Create();

        await maintenance.CompactFromUiAsync();

        Assert.Contains("нечего сжимать", _statuses);
        Assert.Equal(ChatState.Idle, _chat.Current);
        Assert.False(_generating);
    }

    [Fact]
    public async Task ManualFromIdle_BranchSummary_RunsAndReturnsToIdle()
    {
        Fill(12);
        Persist();
        var calls = 0;
        var maintenance = Create(complete: (user, system, onChunk, ct) =>
        {
            calls++;
            // 1-й вызов — суммаризация (отдаём результат), 2-й — извлечение фактов (пусто).
            return Task.FromResult<string?>(calls == 1 ? "итоговое резюме" : null);
        });

        await maintenance.CompactFromUiAsync();

        Assert.Contains("контекст сжат", _statuses.Last());
        Assert.True(_log.Count < 12);           // ранняя часть заменена резюме
        Assert.Equal(ChatState.Idle, _chat.Current);
        Assert.False(_generating);
    }

    [Fact]
    public async Task RequestDuringGenerating_IsQueued_NotExecutedImmediately()
    {
        Fill(12);
        Persist();
        var maintenance = Create();
        _chat.Transition(ChatState.Generating);

        await maintenance.CompactFromUiAsync();

        Assert.Contains("компакция запрошена", _statuses.Last());
        Assert.Equal(12, _log.Count);           // ничего не сжималось
        Assert.Equal(ChatState.Generating, _chat.Current);
    }

    [Fact]
    public async Task QueuedRequest_ExecutesOnBudgetCheck_AndFsmReturnsGenerating()
    {
        Fill(12);
        Persist();
        var calls = 0;
        var maintenance = Create(
            complete: (user, system, onChunk, ct) =>
            {
                calls++;
                return Task.FromResult<string?>(calls == 1 ? "резюме" : null);
            },
            countTokens: ct => Task.FromResult(30000)); // гарантированно не влезает

        _chat.Transition(ChatState.Generating);
        await maintenance.CompactFromUiAsync();   // ставится в очередь

        await maintenance.EnsureBudgetAsync(CancellationToken.None);

        Assert.Contains("контекст сжат", _statuses.Last());
        Assert.True(_log.Count < 12);
        // Авто-компакция между итерациями возвращает FSM в Generating.
        Assert.Equal(ChatState.Generating, _chat.Current);
    }

    [Fact]
    public async Task Failure_ReportsError_LogIntact_FsmBackToIdle()
    {
        Fill(12);
        Persist();
        var maintenance = Create(complete: (user, system, onChunk, ct) =>
            throw new InvalidOperationException("сервер недоступен"));

        await maintenance.CompactFromUiAsync();

        Assert.Contains("ошибка сжатия", _statuses.Last());
        Assert.Equal(12, _log.Count);           // история не тронута
        Assert.Equal(ChatState.Idle, _chat.Current);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
