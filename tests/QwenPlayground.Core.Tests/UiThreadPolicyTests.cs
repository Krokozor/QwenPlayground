using QwenPlayground.Core.Runtime;
using Xunit;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// P4 (внешнее ревью 2026-10-01): механический enforcement инварианта UI-потока.
/// Политика — статика с шнуровкой из App (Dispatcher.CheckAccess); в тестах шнуровки
/// нет (no-op) — здесь проверяем сам контракт: no-op без шнуровки, пропуск на UI-потоке,
/// лог+throw при нарушении, аварийный вентиль ThrowOnViolation=false.
/// </summary>
public sealed class UiThreadPolicyTests : IDisposable
{
    public void Dispose()
    {
        UiThreadPolicy.IsUiThread = null;
        UiThreadPolicy.ThrowOnViolation = true;
    }

    [Fact]
    public void Assert_NoPolicy_IsNoop()
    {
        UiThreadPolicy.IsUiThread = null;
        UiThreadPolicy.Assert("test.no-policy"); // не бросает
    }

    [Fact]
    public void Assert_OnUiThread_Passes()
    {
        UiThreadPolicy.IsUiThread = () => true;
        UiThreadPolicy.Assert("test.on-ui"); // не бросает
    }

    [Fact]
    public void Assert_Violation_LogsAndThrows()
    {
        UiThreadPolicy.IsUiThread = () => false;
        UiThreadPolicy.ThrowOnViolation = true;

        var ex = Assert.Throws<InvalidOperationException>(() => UiThreadPolicy.Assert("test.violation"));
        Assert.Contains("test.violation", ex.Message);
        Assert.Contains("background thread", ex.Message);
    }

    [Fact]
    public void Assert_Violation_LogOnly_WhenThrowDisabled()
    {
        UiThreadPolicy.IsUiThread = () => false;
        UiThreadPolicy.ThrowOnViolation = false;

        UiThreadPolicy.Assert("test.log-only"); // не бросает — только events-лог
    }
}
