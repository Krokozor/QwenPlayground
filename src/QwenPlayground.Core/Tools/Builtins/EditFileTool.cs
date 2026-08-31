using QwenPlayground.Core.Roslyn;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.Tools.Builtins;

[Tool("edit_file", "Replace exact text in a file. old_string must match the file content exactly once.")]
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
        if (matches == 0)
        {
            return "Error: old_string not found in file";
        }
        if (matches > 1)
        {
            return $"Error: old_string matches {matches} times; make it more specific";
        }

        // Атомарная запись (как в write_file): сбой не оставляет полудокумент.
        AtomicFile.WriteAllText(fullPath, content.Replace(OldString, NewString, StringComparison.Ordinal));
        var result = $"edited {context.ToRelative(fullPath)}";
        return await EditDiagnostics.AppendRoslynErrorsAsync(fullPath, result, cancellationToken);
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
