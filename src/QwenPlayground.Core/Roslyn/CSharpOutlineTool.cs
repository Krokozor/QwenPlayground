using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

[Tool("csharp_outline",
    "List types and members of a C# file in the tracked C# project, like a document outline. " +
    "Each line shows the element's line range (':12' or ':12-45' for a multi-line type/method) and " +
    "the first line of the comment above it, so you know what the element is and where it ends " +
    "without reading the file. Path is relative to the working folder.", ToolGroup.CSharp)]
public sealed class CSharpOutlineTool : AgentTool
{
    // Строки с комментариями несут больше смысла, чем раньше, — потолок выше,
    // но обрезка по-прежнему видна модели и подсказывает, как добрать остаток.
    private const int MaxOutputLength = 16000;

    [ToolParameter("File path relative to the working folder, e.g. src/QwenPlayground.Core/Templates/QwenChatTemplate.cs", Required = true)]
    public string Path { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (CSharpProjects.Resolve(context.ProjectRoot, out var error, context.SessionDir) is not { } csharp)
        {
            return error!;
        }
        var solution = await csharp.Service.GetSolutionAsync(cancellationToken);
        // Без проверки границ: трекаемый проект может жить вне рабочей папки.
        var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(context.ProjectRoot, Path));
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (document is null)
        {
            return $"Error: document not found in solution: {Path}";
        }

        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
        if (syntaxRoot is null)
        {
            return "Error: failed to parse document";
        }

        var builder = new StringBuilder();
        foreach (var type in syntaxRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
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
