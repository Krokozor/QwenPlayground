using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

[Tool("csharp_outline",
    "List types and members of a C# file in the QwenPlayground solution, like a document outline. " +
    "Each line shows the element's line range (':12' or ':12-45' for a multi-line type/method) and " +
    "the first line of the comment above it, so you know what the element is and where it ends " +
    "without reading the file.", ToolGroup.CSharp)]
public sealed class CSharpOutlineTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    // Строки с комментариями несут больше смысла, чем раньше, — потолок выше,
    // но обрезка по-прежнему видна модели и подсказывает, как добрать остаток.
    private const int MaxOutputLength = 16000;

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
                     // Только верхний уровень: вложенные типы печатает рекурсия
                     // TypeMapFormatter. В фильтре раньше был ещё и BaseTypeDeclaration —
                     // из-за него вложенный тип выводился дважды (внутри своего контейнера
                     // и отдельным блоком с нулевым отступом).
                     .Where(t => t.Parent is CompilationUnitSyntax
                         or FileScopedNamespaceDeclarationSyntax
                         or NamespaceDeclarationSyntax))
        {
            TypeMapFormatter.AppendType(builder, type, string.Empty);
            if (builder.Length > MaxOutputLength)
            {
                builder.Append("... (truncated — narrow to a single type with csharp_class_map " +
                               "or read_file with offset/limit)");
                return builder.ToString();
            }
        }

        return builder.Length > 0 ? builder.ToString() : "no types found in file";
    }
}
