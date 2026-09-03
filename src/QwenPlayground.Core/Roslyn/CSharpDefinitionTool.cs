using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

[Tool("csharp_definition",
    "Go to definition: given a file, a 1-based line number and the identifier name on that line, " +
    "find where the symbol is declared (kind, signature and file:line).", ToolGroup.CSharp)]
public sealed class CSharpDefinitionTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    [ToolParameter("File path relative to workspace root", Required = true)]
    public string Path { get; set; } = string.Empty;

    [ToolParameter("1-based line number where the identifier is used", Required = true)]
    public int Line { get; set; }

    [ToolParameter("Identifier name on that line, e.g. QwenChatTemplate", Required = true)]
    public string Name { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(SelfBuild.SelfBuildPaths.WorkspaceRoot, Path));
        var document = RoslynService.FindDocument(solution, fullPath);
        if (document is null)
        {
            return $"Error: document not found in solution: {Path}";
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null)
        {
            return "Error: failed to parse document";
        }

        var candidates = root.DescendantTokens()
            .Where(t => t.Text == Name)
            .Where(t => t.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == Line)
            .ToList();
        if (candidates.Count == 0)
        {
            return $"Error: identifier '{Name}' not found on line {Line} of {Path}";
        }
        var token = candidates[0];

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model is null)
        {
            return "Error: failed to get semantic model";
        }
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(model, token.SpanStart, solution.Workspace, cancellationToken);
        if (symbol is null)
        {
            return $"No symbol found for '{Name}' at {Path}:{Line}";
        }

        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var declaration = location is null
            ? "(no source location)"
            : $"{System.IO.Path.GetRelativePath(SelfBuild.SelfBuildPaths.WorkspaceRoot, location.GetLineSpan().Path ?? "?")}:{location.GetLineSpan().StartLinePosition.Line + 1}";
        return $"{symbol.Kind.ToString().ToLowerInvariant()} {symbol.ToDisplayString()} — {declaration}";
    }
}
