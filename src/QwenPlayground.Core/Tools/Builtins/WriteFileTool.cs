using QwenPlayground.Core.Roslyn;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.Tools.Builtins;

[Tool("write_file", "Write text to a file, creating directories if needed. Overwrites the file completely.")]
public sealed class WriteFileTool : AgentTool
{
    [ToolParameter("File path relative to project root", Required = true)]
    public string Path { get; set; } = string.Empty;

    [ToolParameter("Full new content of the file", Required = true)]
    public string Content { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var fullPath = context.ResolvePath(Path);
        // Атомарная запись: сбой посреди записи не должен оставлять полудокумент —
        // он уйдёт и в Roslyn-воркспейс, и в rebuild_self.
        AtomicFile.WriteAllText(fullPath, Content);
        var result = $"wrote {Content.Length} chars to {context.ToRelative(fullPath)}";
        return await EditDiagnostics.AppendRoslynErrorsAsync(fullPath, result, cancellationToken);
    }
}
