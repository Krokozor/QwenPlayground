using System.Text;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Вывод ограничен: длина одной строки (MaxLineChars — минифицированные/сгенерированные
/// файлы) и суммарный размер ответа (MaxOutputChars). Limit ограничивает число строк,
/// но не размер — без капов один файл с мегабайтными строками съедает контекст модели.
/// При обрезке ответ подсказывает продолжить чтение через offset/limit.
///
/// Чтение файла целиком допускается только для маленьких файлов: без явного offset/limit
/// файл крупнее MaxWholeFileBytes не читается вовсе, в ответ уходит его размер, число
/// строк и просьба указать диапазон. Раньше такой вывод «безопасным чтением» уносился
/// в attachments/ сообщения и возвращался превью в 2 КБ — копия файла, который и так
/// лежит на диске, только съедала место и провоцировала второй вызов; модель получала
/// обрывок и угадывала остальное. Теперь файл не читается — и не сохраняется.
/// </summary>
[Tool("read_file", "Read a text file from the project. Returns numbered lines. " +
                   "Without offset/limit only files up to 8 KB are read whole: a bigger file is not " +
                    "read at all — the reply gives its size and line count and asks for a range, so " +
                    "pass offset/limit to read exactly what you need (e.g. offset=1 limit=200). " +
                    "Explicit reads are never auto-capped. Long lines are capped at 400 chars; " +
                    "if truncated, continue with offset.")]
public sealed class ReadFileTool : AgentTool
{
    private const int MaxLineChars = 400;
    private const int MaxOutputChars = 32000;
    private const int DefaultLimit = 400;
    // Патологически большие файлы (логи, дампы) не читаем целиком: ReadAllLines держит
    // их в памяти, а содержимое всё равно не влезет в вывод. Для таких — shell.
    private const long MaxFileBytes = 16 * 1024 * 1024;
    // Порог «прочитать целиком без диапазона». Раньше эту роль играл автокаппинг в
    // AgentLoop: тот же порог, но вывод уходил в attachments/ (бессмысленно для файла,
    // который не меняется) и возвращался обрывком.
    private const long MaxWholeFileBytes = 8 * 1024;

    [ToolParameter("File path relative to project root", Required = true)]
    public string Path { get; set; } = string.Empty;

    /// <summary>Nullable — по нему видно, задал ли модель диапазон явно.</summary>
    [ToolParameter("First line to read (1-based); pass it for a file bigger than 8 KB")]
    public int? Offset { get; set; }

    [ToolParameter("Maximum number of lines to read; pass it for a file bigger than 8 KB")]
    public int? Limit { get; set; }

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var fullPath = context.ResolvePath(Path);
        if (!File.Exists(fullPath))
        {
            return Task.FromResult($"Error: file not found: {Path}");
        }
        var size = new FileInfo(fullPath).Length;
        if (size > MaxFileBytes)
        {
            return Task.FromResult(
                $"Error: file too large ({size / 1024 / 1024} MB > 16 MB cap). " +
                "Use shell to slice it (e.g. Get-Content -TotalCount / -Tail) instead of reading whole.");
        }

        // Диапазон не задан — читаем целиком, но только если файл небольшой.
        if (Offset is null && Limit is null && size > MaxWholeFileBytes)
        {
            return Task.FromResult(TooLargeToReadWhole(context, fullPath, size));
        }

        var lines = File.ReadAllLines(fullPath);
        if (lines.Length == 0)
        {
            return Task.FromResult($"{context.ToRelative(fullPath)} — empty file (0 lines)");
        }
        var requestedOffset = Offset ?? 1;
        if (requestedOffset > lines.Length)
        {
            return Task.FromResult(
                $"{context.ToRelative(fullPath)} — offset {requestedOffset} is beyond the end of file " +
                $"({lines.Length} lines); nothing to read.");
        }
        var offset = Math.Max(requestedOffset, 1);
        var limit = Math.Max(Limit ?? DefaultLimit, 1);
        var end = Math.Min(offset + limit - 1, lines.Length);

        var builder = new StringBuilder();
        builder.Append(context.ToRelative(fullPath)).Append(" — lines ").Append(offset).Append('-').Append(end)
            .Append(" of ").Append(lines.Length).Append('\n');
        var truncated = false;
        var nextOffset = 0;
        for (var i = offset - 1; i < end; i++)
        {
            var line = lines[i];
            var rendered = line.Length > MaxLineChars
                ? line[..MaxLineChars] + "… [+" + (line.Length - MaxLineChars) + " chars]"
                : line;
            if (i > offset - 1 && builder.Length + rendered.Length > MaxOutputChars)
            {
                truncated = true;
                nextOffset = i + 1; // 1-based номер строки, с которой продолжать чтение
                break;
            }
            builder.Append(i + 1).Append(": ").Append(rendered).Append('\n');
        }
        if (truncated)
        {
            builder.Append("... (output cap reached — re-read from line ").Append(nextOffset)
                .Append(" via offset to continue)");
        }

        return Task.FromResult(builder.ToString());
    }

    /// <summary>
    /// Отказ читать большой файл целиком: размер + число строк + просьба о диапазоне.
    /// Файл не читается и не копируется — «безопасное чтение» тут было лишним копированием.
    /// </summary>
    private static string TooLargeToReadWhole(ToolContext context, string fullPath, long size)
    {
        var lines = 0;
        foreach (var _ in File.ReadLines(fullPath))
        {
            lines++;
        }
        // Хвост предлагаем только если он есть: у файла из 100 длинных строк «offset=-299»
        // был бы неуместной подсказкой.
        var tail = lines > DefaultLimit
            ? $", offset={lines - DefaultLimit + 1} limit={DefaultLimit} for the tail"
            : string.Empty;
        return $"{context.ToRelative(fullPath)} — {size / 1024} KB, {lines} lines: too large to read whole. " +
               "Nothing was read and nothing was written — pass offset/limit to read a range " +
               $"(e.g. offset=1 limit={DefaultLimit} for the head{tail}).";
    }
}
