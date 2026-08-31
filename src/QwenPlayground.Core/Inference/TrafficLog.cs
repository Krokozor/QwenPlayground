using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Inference;

public static class TrafficLog
{
    private static readonly object Lock = new();

    public static void Log(string prompt, string output)
    {
        try
        {
            // Один срез времени на запись: иначе на полуночи имя файла и метка внутри разъезжались.
            var now = DateTime.Now;
            var directory = Path.Combine(SelfBuildPaths.WorkspaceRoot, "logs");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"traffic-{now:yyyyMMdd}.log");
            var entry = $"\n===== {now:O} =====\n--- PROMPT ({prompt.Length} chars) ---\n{prompt}\n--- OUTPUT ({output.Length} chars) ---\n{output}\n";
            lock (Lock)
            {
                File.AppendAllText(file, entry);
            }
        }
        catch
        {
        }
    }
}
