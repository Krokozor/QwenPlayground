using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// ShelfState.Activate/Deactivate — общая логика тулов агента (activate/deactivate_shelf)
/// и UI-меню полок: активация немедленная, деактивация staged (pending, снимается
/// FlushPending при естественной смене промпта), пере-активация отменяет pending.
/// </summary>
public sealed class ShelfStateTests : IDisposable
{
    private readonly string _directory;
    private readonly ShelfState _state;
    private readonly List<(ToolGroup Group, string Directory)> _deactivated = new();

    public ShelfStateTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "qwen_shelves_" + Guid.NewGuid().ToString("N"));
        _state = new ShelfState(_directory);
        ShelfState.Deactivated += OnDeactivated;
    }

    private void OnDeactivated(ToolGroup group, string directory) => _deactivated.Add((group, directory));

    public void Dispose()
    {
        ShelfState.Deactivated -= OnDeactivated;
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Activate_AddsGroupToActive()
    {
        Assert.Equal(ShelfResult.Activated, _state.Activate(ToolGroup.Browser));

        Assert.Contains(ToolGroup.Browser, _state.Load());
        Assert.Empty(_state.LoadPending());
    }

    [Fact]
    public void Activate_AlreadyActive_IsNoOp()
    {
        _state.Activate(ToolGroup.Browser);

        Assert.Equal(ShelfResult.AlreadyActive, _state.Activate(ToolGroup.Browser));
        Assert.Contains(ToolGroup.Browser, _state.Load());
    }

    [Fact]
    public void Deactivate_MarksPending_KeepsGroupInActive()
    {
        _state.Activate(ToolGroup.Browser);

        Assert.Equal(ShelfResult.Deactivated, _state.Deactivate(ToolGroup.Browser));

        // Staged: группа остаётся в active (тулзы в промпте), пока не произойдёт FlushPending.
        Assert.Contains(ToolGroup.Browser, _state.Load());
        Assert.Contains(ToolGroup.Browser, _state.LoadPending());
    }

    [Fact]
    public void Deactivate_NotActive_IsNoOp()
    {
        Assert.Equal(ShelfResult.NotActive, _state.Deactivate(ToolGroup.Desktop));
        Assert.Empty(_state.Load());
        Assert.Empty(_state.LoadPending());
    }

    [Fact]
    public void Deactivate_AlreadyPending_IsIdempotent()
    {
        _state.Activate(ToolGroup.Browser);
        _state.Deactivate(ToolGroup.Browser);

        Assert.Equal(ShelfResult.Deactivated, _state.Deactivate(ToolGroup.Browser));
        Assert.Contains(ToolGroup.Browser, _state.Load());
        Assert.Contains(ToolGroup.Browser, _state.LoadPending());
    }

    [Fact]
    public void Activate_PendingGroup_CancelsDeactivation()
    {
        _state.Activate(ToolGroup.Browser);
        _state.Deactivate(ToolGroup.Browser);

        Assert.Equal(ShelfResult.Reactivated, _state.Activate(ToolGroup.Browser));

        // Решение на выключение откатилось: группа активна, pending пуст, промпт не менялся.
        Assert.Contains(ToolGroup.Browser, _state.Load());
        Assert.Empty(_state.LoadPending());
    }

    [Fact]
    public void FlushPending_RemovesPendingGroupsFromActive()
    {
        _state.Activate(ToolGroup.Browser);
        _state.Activate(ToolGroup.CSharp);
        _state.Deactivate(ToolGroup.Browser);

        var removed = _state.FlushPending().ToList();

        Assert.Equal(new[] { ToolGroup.Browser }, removed);
        Assert.DoesNotContain(ToolGroup.Browser, _state.Load());
        Assert.Contains(ToolGroup.CSharp, _state.Load());
        Assert.Empty(_state.LoadPending());
    }

    [Fact]
    public void FlushPending_Empty_IsNoOp()
    {
        _state.Activate(ToolGroup.Browser);

        Assert.Empty(_state.FlushPending());
        Assert.Contains(ToolGroup.Browser, _state.Load());
    }

    [Fact]
    public void Deactivate_FiresEvent_WithGroupAndSessionDirectory()
    {
        _state.Activate(ToolGroup.Desktop);

        _state.Deactivate(ToolGroup.Desktop);

        // Доменное событие «полка снята» — единственная точка, где UI реагирует
        // (например, скрывает оверлей курсора), а не прячет реакцию в вызывающих местах.
        Assert.Single(_deactivated);
        Assert.Equal(ToolGroup.Desktop, _deactivated[0].Group);
        Assert.Equal(_directory, _deactivated[0].Directory);
    }

    [Fact]
    public void Deactivate_AlreadyPending_DoesNotRefireEvent()
    {
        _state.Activate(ToolGroup.Browser);
        _state.Deactivate(ToolGroup.Browser);

        _state.Deactivate(ToolGroup.Browser); // идемпотентная пометка — без повторного события

        Assert.Single(_deactivated);
    }

    [Fact]
    public void Deactivate_NotActive_DoesNotFireEvent()
    {
        Assert.Equal(ShelfResult.NotActive, _state.Deactivate(ToolGroup.Desktop));

        Assert.Empty(_deactivated);
    }
}
