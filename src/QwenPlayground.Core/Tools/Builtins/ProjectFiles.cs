using Microsoft.Extensions.FileSystemGlobbing;

namespace QwenPlayground.Core.Tools.Builtins;

internal static class ProjectFiles
{
    private static readonly string[] SkippedDirectories = [".git", "bin", "obj", "node_modules", ".vs", ".idea"];

    public static IEnumerable<string> Enumerate(string projectRoot, string? includePattern = null)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(includePattern is { Length: > 0 } ? includePattern : "**/*");

        foreach (var path in matcher.GetResultsInFullPath(projectRoot))
        {
            if (IsSkipped(projectRoot, path))
            {
                continue;
            }
            yield return path;
        }
    }

    private static bool IsSkipped(string projectRoot, string path)
    {
        var relative = Path.GetRelativePath(projectRoot, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var segment in segments)
        {
            foreach (var skipped in SkippedDirectories)
            {
                if (segment.Equals(skipped, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
