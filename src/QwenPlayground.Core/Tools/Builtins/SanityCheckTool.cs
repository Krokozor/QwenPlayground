using QwenPlayground.Core.Probes;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Самопроверка в длинном ходе (sanity-check): агент описывает, что делает и есть ли прогресс.
/// Сбрасывает nag-счётчик в AgentLoop. Отписка пишется в logs/sanity.md — мини-журнал траектории.
/// Дополнительно — ординальная проба на компаньон-модели: оценка 0–9 + энтропия (уверенность).
/// </summary>
[Tool("sanity_check", "Self-check during a long turn: describe what you are doing and whether there is progress. Resets the nag counter; the reply is journaled to logs/sanity.md.")]
public sealed class SanityCheckTool : AgentTool
{
    [ToolParameter("Briefly: what are you doing right now, is there progress, should you change strategy?", Required = true)]
    public string WhatIAmDoing { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var text = WhatIAmDoing.Trim();
        if (text.Length == 0)
        {
            return "sanity_check: поле пусто — опиши, что ты делаешь.";
        }

        // 1. Журнал отписок — мини-журнал траектории (для меня и для владельца).
        try
        {
            var journalDir = Path.Combine(SelfBuildPaths.WorkspaceRoot, "logs");
            Directory.CreateDirectory(journalDir);
            File.AppendAllText(Path.Combine(journalDir, "sanity.md"),
                $"- [{DateTime.Now:yyyy-MM-dd HH:mm}] {text}\n");
        }
        catch
        {
            // Журнал не критичен — не роняем самопроверку из-за файловой ошибки.
        }

        // 2. Ординальная проба на компаньон-модели (отдельная машина — не трогает наш KV-кеш).
        var settings = AppSettings.Get();
        if (string.IsNullOrWhiteSpace(settings.CompanionEndpoint))
        {
            return "Самопроверка записана (logs/sanity.md). Проба недоступна: CompanionEndpoint не задан.";
        }

        try
        {
            var probePrompt = "Rate on a scale of 0–9 how much the work has progressed toward its goal in the latest actions:\n" +
                              $"\"{text}\"\nReply with a single digit and nothing else.";
            var probe = await LlmProbeClient.ProbeAsync(settings.CompanionEndpoint, probePrompt, nProbs: 12, cancellationToken);
            var assessment = probe.ArgmaxToken.Length == 1 && char.IsDigit(probe.ArgmaxToken[0])
                ? $"{probe.ArgmaxToken}/9"
                : $"неочевидный токен «{probe.ArgmaxToken}»";
            var confidence = probe.Entropy < 1.0 ? "уверенно" : "неуверенно";
            return $"Самопроверка записана (logs/sanity.md). Оценка прогресса компаньоном: {assessment} (энтропия {probe.Entropy:F2}, {confidence}).";
        }
        catch (Exception exception)
        {
            return $"Самопроверка записана (logs/sanity.md). Проба не удалась: {exception.Message}";
        }
    }
}
