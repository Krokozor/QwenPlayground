namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Пауза на N секунд: время должно реально пройти, а загрузка страницы это не покрывает —
/// таймеры обратного отсчёта, капчи «посмотри рекламу X секунд и ссылка станет доступна»,
/// rate-limit'ы, длинные анимации. Не путать с browser_wait (ожидание элемента на странице).
/// </summary>
[Tool("sleep", "Wait (do nothing) for N seconds. Use when real time must pass and page loading doesn't cover it: " +
               "countdown timers, 'watch the ad for X seconds' captchas, rate limits, long animations. " +
               "Max 300 seconds. For waiting on a page element to appear/disappear, use browser_wait instead.")]
public sealed class SleepTool : AgentTool
{
    [ToolParameter("Seconds to wait (1-300)", Required = true)]
    public int Seconds { get; set; } = 5;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var s = Math.Clamp(Seconds, 1, 300);
        await Task.Delay(s * 1000, cancellationToken);
        return $"Slept {s}s.";
    }
}
