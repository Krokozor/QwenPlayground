using QwenPlayground.Core.Roslyn;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.Tools.Builtins;

[Tool("edit_file", "Replace exact text in a file. old_string must match the file content exactly once. Line endings are tolerated: if the exact match fails, both the file and old_string are normalized to LF before matching (handles CRLF and mixed-EOL files); the result is written back with the file's dominant EOL.")]
public sealed class EditFileTool : AgentTool
{
    [ToolParameter("File path relative to project root", Required = true)]
    public string Path { get; set; } = string.Empty;

    [ToolParameter("Exact text to find", Required = true)]
    public string OldString { get; set; } = string.Empty;

    [ToolParameter("Replacement text", Required = true)]
    public string NewString { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var fullPath = context.ResolvePath(Path);
        if (!File.Exists(fullPath))
        {
            return $"Error: file not found: {Path}";
        }

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var matches = CountOccurrences(content, OldString);
        if (matches == 1)
        {
            // Точное совпадение — обычная замена.
            AtomicFile.WriteAllText(fullPath, content.Replace(OldString, NewString, StringComparison.Ordinal));
            var result = $"edited {context.ToRelative(fullPath)}";
            return await EditDiagnostics.AppendRoslynErrorsAsync(fullPath, result, cancellationToken);
        }
        if (matches > 1)
        {
            return $"Error: old_string matches {matches} times; make it more specific";
        }

        // Точного совпадения нет: файл, вероятно, с CRLF (или смешанными) окончаниями,
        // а old_string передан с LF. Повторяем с нормализацией всего к LF;
        // результат записываем с доминирующим EOL файла (единые окончания в итоге).
        string eol = DominantEol(content);
        string normContent = content.Replace("\r\n", "\n");
        string normOld = OldString.Replace("\r\n", "\n");
        string normNew = NewString.Replace("\r\n", "\n");
        int normMatches = CountOccurrences(normContent, normOld);
        if (normMatches == 0)
        {
            return "Error: old_string not found in file";
        }
        if (normMatches > 1)
        {
            return $"Error: old_string matches {normMatches} times; make it more specific";
        }

        string replaced = normContent.Replace(normOld, normNew, StringComparison.Ordinal);
        if (eol == "\r\n")
        {
            replaced = replaced.Replace("\n", "\r\n");
        }

        // Атомарная запись (как в write_file): сбой не оставляет полудокумент.
        AtomicFile.WriteAllText(fullPath, replaced);
        var editResult = $"edited {context.ToRelative(fullPath)}";
        return await EditDiagnostics.AppendRoslynErrorsAsync(fullPath, editResult, cancellationToken);
    }

    /// <summary>Доминирующее окончание строки файла: CRLF, если CRLF-строк не меньше одиночных LF.</summary>
    private static string DominantEol(string content)
    {
        int crlf = CountOccurrences(content, "\r\n");
        int lf = content.Length - content.Replace("\n", string.Empty).Length;
        int bareLf = lf - crlf;
        return crlf >= bareLf ? "\r\n" : "\n";
    }

    private static int CountOccurrences(string text, string value)
    {
        if (value.Length == 0)
        {
            return 0;
        }
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
