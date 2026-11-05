namespace QwenPlayground.Core.Tools.Builtins;

[Tool("glob", "Find files by glob pattern relative to project root, e.g. src/**/*.cs. " +
              "For more than 200 matches the full list is attached and the reply shows the first 200.")]
public sealed class GlobTool : AgentTool
{
    private const int MaxResults = 200;
    private StagedTextArtifact? _artifact;

    [ToolParameter("Glob pattern", Required = true)]
    public string Pattern { get; set; } = string.Empty;

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var preview = new List<string>(MaxResults);
        var total = 0;
        using var artifact = new StagedTextArtifact(context, "glob.txt");
        foreach (var path in ProjectFiles.Enumerate(context.ProjectRoot, Pattern))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(path, artifact.PendingPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var relative = context.ToRelative(path);
            artifact.WriteLine(relative);
            if (preview.Count < MaxResults)
            {
                preview.Add(relative);
            }
            total++;
        }
        if (total == 0)
        {
            return Task.FromResult("no files matched");
        }

        var text = string.Join('\n', preview);
        if (total > preview.Count)
        {
            if (context.SessionDir is not null)
            {
                artifact.KeepForFinalize();
                _artifact = artifact;
                text += $"\n... ({total - preview.Count} more — full {total} results attached.)";
            }
            else
            {
                text += $"\n... ({total - preview.Count} more — narrow the pattern)";
            }
        }
        return Task.FromResult(text);
    }

    public override Task FinalizeAsync(ToolContext context, int messageId, CancellationToken cancellationToken)
    {
        if (_artifact is null)
        {
            return Task.CompletedTask;
        }
        var full = _artifact.Finish(messageId, context);
        _artifact = null;
        var message = context.Conversation?.FirstOrDefault(candidate => candidate.Id == messageId);
        if (message is not null && full.Length > 0)
        {
            message.Content += "\n" + StagedTextArtifact.AttachmentTag(full);
        }
        return Task.CompletedTask;
    }
}
