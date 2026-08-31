using System.Text;
using System.Text.RegularExpressions;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Вывод ограничен по трём осям: число матчей (MaxMatches), длина одной строки
/// (MaxLineChars — минифицированные/сгенерированные файлы) и суммарный размер ответа
/// (MaxOutputChars). Иначе поиск по крупному проекту возвращает простыню, съедающую
/// контекст модели. Лимиты жёсткие сознательно: лучше недо-выдать с пометкой — модель
/// уточнит паттерн или добавит include-фильтр.
/// </summary>
[Tool("grep", "Search file contents with a regex. Returns matching lines as path:line: text. " +
              "Output is capped (max 100 matches / ~8KB / 240 chars per line); if truncated, " +
              "narrow the pattern or add an include filter.")]
public sealed class GrepTool : AgentTool
{
    private const int MaxMatches = 100;
    private const int MaxLineChars = 240;
    private const int MaxOutputChars = 8000;
    private const long MaxFileBytes = 2 * 1024 * 1024;

    // Паттерн приходит от модели: без таймаута катастрофический бэктрекинг ((a+)+$)
    // повесил бы агентный цикл навсегда.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    [ToolParameter("Regex pattern to search for", Required = true)]
    public string Pattern { get; set; } = string.Empty;

    [ToolParameter("Optional glob filter for files, e.g. *.cs or src/**/*.cs")]
    public string? Include { get; set; }

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        Regex regex;
        try
        {
            regex = new Regex(Pattern, RegexOptions.Compiled, MatchTimeout);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult($"Error: invalid regex: {exception.Message}");
        }

        var builder = new StringBuilder();
        var matchCount = 0;
        var truncated = false;
        foreach (var path in ProjectFiles.Enumerate(context.ProjectRoot, Include))
        {
            if (matchCount >= MaxMatches || builder.Length >= MaxOutputChars)
            {
                truncated = true;
                break;
            }
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                continue;
            }
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Файл мог быть удалён/залочен между перечислением и чтением — пропускаем.
                continue;
            }
            for (var i = 0; i < lines.Length; i++)
            {
                if (matchCount >= MaxMatches || builder.Length >= MaxOutputChars)
                {
                    truncated = true;
                    break;
                }
                if (!regex.IsMatch(lines[i]))
                {
                    continue;
                }
                matchCount++;
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }
                builder.Append(context.ToRelative(path)).Append(':').Append(i + 1).Append(": ")
                    .Append(TrimLine(lines[i]));
            }
            if (truncated)
            {
                break;
            }
        }

        if (matchCount == 0)
        {
            return Task.FromResult("no matches");
        }
        if (truncated)
        {
            builder.Append("\n... (truncated at ").Append(matchCount)
                .Append(" matches — output cap reached; narrow the pattern or add an include filter)");
        }
        return Task.FromResult(builder.ToString());
    }

    /// <summary>Длинная строка матча (минифицированный JS, длинная строка кода) — обрезается до MaxLineChars.</summary>
    private static string TrimLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= MaxLineChars ? trimmed : trimmed[..MaxLineChars] + "…";
    }
}
