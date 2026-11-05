using System.Text;
using System.Text.RegularExpressions;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>В чат идёт ограниченное превью, полный список совпадений сохраняется в артефакт.</summary>
[Tool("grep", "Search file contents with a regex. Returns matching lines as path:line: text. " +
              "The preview is capped (max 100 matches / ~8KB / 240 chars per line); " +
              "when capped, the complete result is attached.")]
public sealed class GrepTool : AgentTool
{
    private const int MaxMatches = 100;
    private const int MaxLineChars = 240;
    private const int MaxOutputChars = 8000;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
    private StagedTextArtifact? _artifact;

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
        var previewCount = 0;
        var truncated = false;
        using var artifact = new StagedTextArtifact(context, "grep.txt");
        foreach (var path in ProjectFiles.Enumerate(context.ProjectRoot, Include))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(path, artifact.PendingPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (truncated && context.SessionDir is null)
            {
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
                continue;
            }
            for (var i = 0; i < lines.Length; i++)
            {
                if ((i & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (!regex.IsMatch(lines[i]))
                {
                    continue;
                }
                matchCount++;
                var fullLine = $"{context.ToRelative(path)}:{i + 1}: {lines[i].Trim()}";
                artifact.WriteLine(fullLine);
                if (previewCount < MaxMatches && builder.Length < MaxOutputChars)
                {
                    previewCount++;
                    if (builder.Length > 0)
                    {
                        builder.Append('\n');
                    }
                    builder.Append(context.ToRelative(path)).Append(':').Append(i + 1).Append(": ")
                        .Append(TrimLine(lines[i]));
                }
                else
                {
                    truncated = true;
                    if (context.SessionDir is null)
                    {
                        break;
                    }
                }
            }
        }

        if (matchCount == 0)
        {
            return Task.FromResult("no matches");
        }
        if (truncated)
        {
            if (context.SessionDir is not null)
            {
                artifact.KeepForFinalize();
                _artifact = artifact;
                builder.Append("\n... (showing ").Append(previewCount).Append(" of ").Append(matchCount)
                    .Append(" matches — full results attached.)");
            }
            else
            {
                builder.Append("\n... (truncated at ").Append(previewCount)
                    .Append(" matches — output cap reached; narrow the pattern or add an include filter)");
            }
        }
        return Task.FromResult(builder.ToString());
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

    private static string TrimLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= MaxLineChars ? trimmed : trimmed[..MaxLineChars] + "…";
    }
}
