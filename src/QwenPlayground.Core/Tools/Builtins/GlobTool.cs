namespace QwenPlayground.Core.Tools.Builtins;

[Tool("glob", "Find files by glob pattern relative to project root, e.g. src/**/*.cs")]
public sealed class GlobTool : AgentTool
{
    private const int MaxResults = 200;

    [ToolParameter("Glob pattern", Required = true)]
    public string Pattern { get; set; } = string.Empty;

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var all = ProjectFiles.Enumerate(context.ProjectRoot, Pattern)
            .Select(context.ToRelative)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (all.Count == 0)
        {
            return Task.FromResult("no files matched");
        }
        var shown = all.Take(MaxResults).ToList();
        // Хвост с числом невыдаанных файлов: модель должна видеть, что список неполный.
        var text = shown.Count < all.Count
            ? string.Join('\n', shown) + $"\n... ({all.Count - shown.Count} more — narrow the pattern)"
            : string.Join('\n', shown);
        return Task.FromResult(text);
    }
}
