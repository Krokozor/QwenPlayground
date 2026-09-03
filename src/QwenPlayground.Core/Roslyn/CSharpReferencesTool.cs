using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

[Tool("csharp_references",
    "Find all references (usages) of a C# symbol by name in the QwenPlayground solution. " +
    "Returns file:line and a code snippet for each usage. Semantic search — no false positives from text grep.", ToolGroup.CSharp)]
public sealed class CSharpReferencesTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    private const int MaxReferences = 50;
    private const int MaxSnippetLength = 120;

    [ToolParameter("Symbol name to find references for, e.g. QwenChatTemplate or Render", Required = true)]
    public string Name { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        var seen = new HashSet<string>();
        var results = new List<string>();
        var textCache = new Dictionary<DocumentId, SourceText>();
        var done = false;

        foreach (var project in solution.Projects)
        {
            if (done)
            {
                break;
            }
            var declarations = await SymbolFinder.FindDeclarationsAsync(project, Name, ignoreCase: false, SymbolFilter.All, cancellationToken);
            foreach (var symbol in declarations)
            {
                // Перегрузка (ISymbol, Solution): ищем ссылки по всему солюшену,
                // в том числе в других проектах.
                var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
                foreach (var reference in references)
                {
                    foreach (var referenceLocation in reference.Locations)
                    {
                        if (!referenceLocation.Location.IsInSource)
                        {
                            continue;
                        }
                        var position = referenceLocation.Location.GetLineSpan();
                        var path = Path.GetRelativePath(SelfBuild.SelfBuildPaths.WorkspaceRoot, position.Path ?? "?");
                        if (!seen.Add($"{path}:{position.StartLinePosition}"))
                        {
                            continue;
                        }
                        var snippet = await GetSnippetAsync(referenceLocation.Document, textCache, position.StartLinePosition.Line, cancellationToken);
                        results.Add($"{path}:{position.StartLinePosition.Line + 1}: {snippet}");
                        if (results.Count >= MaxReferences)
                        {
                            done = true;
                            break;
                        }
                    }
                    if (done)
                    {
                        break;
                    }
                }
            }
        }

        if (results.Count == 0)
        {
            return $"no references to '{Name}' found";
        }
        var builder = new StringBuilder(string.Join('\n', results));
        if (results.Count >= MaxReferences)
        {
            builder.Append("\n... (truncated at 50 references)");
        }
        return builder.ToString();
    }

    private static async Task<string> GetSnippetAsync(
        Document document, Dictionary<DocumentId, SourceText> cache, int line, CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(document.Id, out var text))
        {
            text = await document.GetTextAsync(cancellationToken);
            cache[document.Id] = text;
        }
        var lineText = text.Lines[line].ToString().Trim();
        return lineText.Length > MaxSnippetLength ? lineText[..MaxSnippetLength] + "…" : lineText;
    }
}
