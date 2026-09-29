using QwenPlayground.Core.Inference;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Трекер использования слотов (фаза 3, план 2026-09-28): владелец (id сессии /
/// «пробы») в метке «последнее использование» — чей это KV видно в «Диагностике».
/// </summary>
public sealed class SlotUsageTrackerTests
{
    [Fact]
    public void Record_StoresTimeAndOwner()
    {
        var before = DateTime.Now;
        SlotUsageTracker.Record(9, "session-abc");

        var use = SlotUsageTracker.LastUse(9);

        Assert.NotNull(use);
        Assert.Equal("session-abc", use!.Owner);
        Assert.InRange(use.Time, before, DateTime.Now);
    }

    [Fact]
    public void Record_SameSlot_OverwritesOwner()
    {
        SlotUsageTracker.Record(8, "first");
        SlotUsageTracker.Record(8, "second");

        Assert.Equal("second", SlotUsageTracker.LastUse(8)!.Owner);
    }

    [Fact]
    public void LastUse_UntouchedSlot_IsNull()
    {
        Assert.Null(SlotUsageTracker.LastUse(-42));
    }
}
