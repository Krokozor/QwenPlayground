using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

[Tool("csharp_class_map",
    "Show the blueprint of a C# type by name in the tracked C# project: where it is declared and " +
    "all its members with line ranges (':12' or ':12-45' for a multi-line member) and the first " +
    "line of the comment above each. Use to get a map of a class without reading the whole file " +
    "(query by name, no need to know the file path). Paths are relative to the working folder.",
    ToolGroup.CSharp)]
public sealed class CSharpClassMapTool : AgentTool
{
    private const int MaxMatches = 3;
    private const int MaxOutputLength = 16000;

    [ToolParameter("Type name, e.g. QwenChatTemplate", Required = true)]
    public string Name { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (CSharpProjects.Resolve(context.ProjectRoot, out var error, context.SessionDir) is not { } csharp)
        {
            return error!;
        }
        var solution = await csharp.Service.GetSolutionAsync(cancellationToken);
        var root = context.ProjectRoot;
        var builder = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
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
                // Один тип находится столько раз, сколько проектов ссылается на его
                // проект: без дедупа по файлу «чертёж» печатался трижды подряд.
                if (!seen.Add(span.Path ?? string.Empty))
                {
                    continue;
                }
                var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
                if (syntaxRoot is null)
                {
                    continue;
                }
                var line = span.StartLinePosition.Line;
                var node = syntaxRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
                    .FirstOrDefault(t => t.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line == line);
                if (node is null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }
                builder.Append(Relative(span.Path ?? "?", root)).Append('\n');
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
            builder.Append("\n... (truncated — read the file with read_file offset/limit for the rest)");
        }
        return builder.ToString();
    }

    /// <summary>Путь относительно рабочей папки; разные диски — абсолютный (честно).</summary>
    private static string Relative(string path, string root)
    {
        try
        {
            return System.IO.Path.GetRelativePath(root, path);
        }
        catch
        {
            return path;
        }
    }
}
