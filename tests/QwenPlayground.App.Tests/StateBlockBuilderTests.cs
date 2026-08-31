using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Memory;

namespace QwenPlayground.App.Tests;

/// <summary>
/// Снапшот самосостояния агента: инварианты полей блока на подставных данных.
/// BuildJournal в тестовой среде недоступен (нет развёрнутого run/) — BuildId/Status null.
/// </summary>
public sealed class StateBlockBuilderTests
{
    private readonly ServerProps _serverProps = new();
    private readonly ChatLog _log = new();
    private readonly List<SurfacedMemory> _surfaced = [];
    private readonly List<QwenPlayground.Core.Memory.PendingPair> _pendingPairs = [];
    private string? _nag;
    private int _effectiveSize = 32768;

    private StateBlockBuilder Create()
    {
        return new StateBlockBuilder(
            _log.AssignPendingIds,
            () => _log.NextMessageId,
            () => _effectiveSize,
            _serverProps,
            () => _log,
            () => _surfaced,
            () => _nag,
            () => _pendingPairs);
    }

    [Fact]
    public void PendingPairs_BudgetThree_PerRender()
    {
        var builder = Create();
        for (var i = 0; i < 5; i++)
        {
            _pendingPairs.Add(new QwenPlayground.Core.Memory.PendingPair($"mem{i}a", $"mem{i}b", 0.9, new double[] { 0, 0, 0, 0, 0, 0, 0, 1, 0, 0 }));
        }

        var block = builder.Build();

        Assert.Equal(3, block.SimilarPairs.Count); // очередь не съедает контекст
        Assert.Equal(("mem0a", "mem0b"), (block.SimilarPairs[0].A, block.SimilarPairs[0].B));
    }

    [Fact]
    public void MsgId_EqualsNextMessageId_AfterPendingAssignment()
    {
        var builder = Create();
        _log.Add(new ChatMessage { Role = ChatRole.User, Content = "вопрос" });

        var block = builder.Build();

        Assert.Equal(_log.NextMessageId, block.MsgId); // ID, который получит ответ
        Assert.Equal(2, block.MsgId);
    }

    [Fact]
    public void Context_ClampedToEffectiveWindow_UnknownShownAsZero()
    {
        var builder = Create();
        _log.Add(new ChatMessage { Role = ChatRole.User, Content = "в" });

        // Сервер не отвечал → 0 («неизвестно»).
        var unknown = builder.Build();
        Assert.Equal(0, unknown.ContextUsed);
        Assert.Equal(32768, unknown.ContextMax);

        _serverProps.SetLastPromptTokens(40000); // факт больше окна — клэмп
        _effectiveSize = 8192;
        var clamped = builder.Build();
        Assert.Equal(8192, clamped.ContextUsed);
        Assert.Equal(8192, clamped.ContextMax);
    }

    [Fact]
    public void SurfacedMemories_FlattenedAndTruncated()
    {
        var builder = Create();
        _surfaced.Add(new SurfacedMemory("mem1", new string('д', 300), 0.9));

        var block = builder.Build();

        var memory = Assert.Single(block.Memories);
        Assert.Equal("mem1", memory.Id);
        Assert.Equal(200 + 1, memory.Content!.Length); // 200 символов + многоточие
        Assert.DoesNotContain('\n', memory.Content);
        Assert.Equal(0.9, memory.Relevance);
    }

    [Fact]
    public void MemoryNag_PassedThrough_WhenPresent()
    {
        var builder = Create();
        _nag = "займись дедупом памяти";

        var block = builder.Build();

        Assert.Equal("займись дедупом памяти", block.MemoryNag);
    }

    [Fact]
    public void BuildInfo_Null_WithoutDeployedRun()
    {
        var builder = Create();
        _log.Add(new ChatMessage { Role = ChatRole.User, Content = "в" });

        var block = builder.Build();

        // В тестовой среде нет run/<id>/journal.json.
        Assert.Null(block.BuildId);
        Assert.Null(block.BuildStatus);
    }
}
