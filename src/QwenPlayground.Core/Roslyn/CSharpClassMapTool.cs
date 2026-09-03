using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

[Tool("csharp_class_map",
    "Show the blueprint of a C# type by name: where it is declared and all its members with line numbers. " +
    "Use to get a map of a class without reading the whole file (query by name, no need to know the file path).", ToolGroup.CSharp)]
public sealed class CSharpClassMapTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    private const int MaxMatches = 3;
    private const int MaxOutputLength = 8000;

    [ToolParameter("Type name, e.g. QwenChatTemplate", Required = true)]
    public string Name { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        var builder = new StringBuilder();
        var found = 0;

        foreach (var project in solution.Projects)
        {
            if (found >= MaxMatches)
            {
                break;
            }
            var types = await SymbolFinder.FindDeclarationsAsync(project, Name, ignoreCase: false, SymbolFilter.Type, cancellationToken);
            foreach (var type in types)
            {
                var location = type.Locations.FirstOrDefault(l => l.IsInSource);
                if (location is null)
                {
                    continue;
                }
                var span = location.GetLineSpan();
                var document = RoslynService.FindDocument(solution, span.Path ?? string.Empty);
                if (document is null)
                {
                    continue;
                }
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root is null)
                {
                    continue;
                }
                var line = span.StartLinePosition.Line;
                var node = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                    .FirstOrDefault(t => t.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line == line);
                if (node is null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }
                builder.Append(System.IO.Path.GetRelativePath(SelfBuild.SelfBuildPaths.WorkspaceRoot, span.Path ?? "?")).Append('\n');
                TypeMapFormatter.AppendType(builder, node, string.Empty);
                found++;
                if (found >= MaxMatches || builder.Length > MaxOutputLength)
                {
                    break;
                }
            }
        }

        if (found == 0)
        {
            return $"no type named '{Name}' found";
        }
        if (builder.Length > MaxOutputLength)
        {
            builder.Length = MaxOutputLength;
            builder.Append("\n... (truncated)");
        }
        return builder.ToString();
    }
}
