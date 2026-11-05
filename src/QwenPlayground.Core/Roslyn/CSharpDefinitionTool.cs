using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

[Tool("csharp_definition",
    "Go to definition: given a file, a 1-based line number and the identifier name on that line, " +
    "find where the symbol is declared in the tracked C# project (kind, signature and file:line — " +
    "for a multi-line declaration the whole line range file:12-45 — plus the first line of the " +
    "comment above it). Paths are relative to the working folder.",
    ToolGroup.CSharp)]
public sealed class CSharpDefinitionTool : AgentTool
{
    [ToolParameter("File path relative to the working folder", Required = true)]
    public string Path { get; set; } = string.Empty;

    [ToolParameter("1-based line number where the identifier is used", Required = true)]
    public int Line { get; set; }

    [ToolParameter("Identifier name on that line, e.g. QwenChatTemplate", Required = true)]
    public string Name { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (CSharpProjects.Resolve(context.ProjectRoot, out var error) is not { } csharp)
        {
            return error!;
        }
        var solution = await csharp.Service.GetSolutionAsync(cancellationToken);
        var root = context.ProjectRoot;
        // Без проверки границ: трекаемый проект может жить вне рабочей папки (Unity: sln наверху).
        var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, Path));
        var document = RoslynService.FindDocument(solution, fullPath);
        if (document is null)
        {
            return $"Error: document not found in solution: {Path}";
        }

        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
        if (syntaxRoot is null)
        {
            return "Error: failed to parse document";
        }

        var candidates = syntaxRoot.DescendantTokens()
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

        return await LocationFormatter.DeclarationAsync(symbol, root, cancellationToken);
    }
}
