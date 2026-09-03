using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

[Tool("csharp_outline", "List types and members of a C# file in the QwenPlayground solution, like a document outline.", ToolGroup.CSharp)]
public sealed class CSharpOutlineTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    [ToolParameter("File path relative to workspace root, e.g. src/QwenPlayground.Core/Templates/QwenChatTemplate.cs", Required = true)]
    public string Path { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(SelfBuild.SelfBuildPaths.WorkspaceRoot, Path));
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (document is null)
        {
            return $"Error: document not found in solution: {Path}";
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null)
        {
            return "Error: failed to parse document";
        }

        var builder = new StringBuilder();
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                     .Where(t => t.Parent is CompilationUnitSyntax or
                         FileScopedNamespaceDeclarationSyntax or
                         NamespaceDeclarationSyntax or
                         BaseTypeDeclarationSyntax))
        {
            TypeMapFormatter.AppendType(builder, type, string.Empty);
            if (builder.Length > 8000)
            {
                builder.Append("... (truncated)");
                return builder.ToString();
            }
        }

        return builder.Length > 0 ? builder.ToString() : "no types found in file";
    }
}
