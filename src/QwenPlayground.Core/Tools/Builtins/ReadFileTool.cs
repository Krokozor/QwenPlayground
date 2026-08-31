using System.Text;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Вывод ограничен: длина одной строки (MaxLineChars — минифицированные/сгенерированные
/// файлы) и суммарный размер ответа (MaxOutputChars). Limit ограничивает число строк,
/// но не размер — без капов один файл с мегабайтными строками съедает контекст модели.
/// При обрезке ответ подсказывает продолжить чтение через offset/limit.
/// </summary>
[Tool("read_file", "Read a text file from the project. Returns numbered lines. " +
                   "Long lines and total output are capped; if truncated, continue with offset.")]
public sealed class ReadFileTool : AgentTool
{
    private const int MaxLineChars = 400;
    private const int MaxOutputChars = 32000;
    // Патологически большие файлы (логи, дампы) не читаем целиком: ReadAllLines держит
    // их в памяти, а содержимое всё равно не влезет в вывод. Для таких — shell.
    private const long MaxFileBytes = 16 * 1024 * 1024;

    [ToolParameter("File path relative to project root", Required = true)]
    public string Path { get; set; } = string.Empty;

    [ToolParameter("First line to read (1-based)")]
    public int Offset { get; set; } = 1;

    [ToolParameter("Maximum number of lines to read")]
    public int Limit { get; set; } = 400;

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var fullPath = context.ResolvePath(Path);
        if (!File.Exists(fullPath))
        {
            return Task.FromResult($"Error: file not found: {Path}");
        }
        if (new FileInfo(fullPath).Length > MaxFileBytes)
        {
            return Task.FromResult(
                $"Error: file too large ({new FileInfo(fullPath).Length / 1024 / 1024} MB > 16 MB cap). " +
                "Use shell to slice it (e.g. Get-Content -TotalCount / -Tail) instead of reading whole.");
        }

        var lines = File.ReadAllLines(fullPath);
        var offset = Math.Clamp(Offset, 1, Math.Max(lines.Length, 1));
        var limit = Math.Max(Limit, 1);
        var end = Math.Min(offset + limit - 1, lines.Length);

        var builder = new StringBuilder();
        builder.Append(context.ToRelative(fullPath)).Append(" — lines ").Append(offset).Append('-').Append(end)
            .Append(" of ").Append(lines.Length).Append('\n');
        var truncated = false;
        var nextOffset = 0;
        for (var i = offset - 1; i < end; i++)
        {
            var line = lines[i];
            if (builder.Length + line.Length > MaxOutputChars && builder.Length > 0)
            {
                truncated = true;
                nextOffset = i + 1; // 1-based номер строки, с которой продолжать чтение
                break;
            }
            builder.Append(i + 1).Append(": ");
            if (line.Length > MaxLineChars)
            {
                builder.Append(line[..MaxLineChars]).Append("… [+").Append(line.Length - MaxLineChars).Append(" chars]");
            }
            else
            {
                builder.Append(line);
            }
            builder.Append('\n');
        }
        if (truncated)
        {
            builder.Append("... (output cap reached — re-read from line ").Append(nextOffset)
                .Append(" via offset to continue)");
        }

        return Task.FromResult(builder.ToString());
    }
}
