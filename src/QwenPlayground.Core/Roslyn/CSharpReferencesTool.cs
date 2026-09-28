using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// Ссылки на символ, сгруппированные по декларации: заголовок группы несёт декларацию
/// (с диапазоном строк и комментарием), строки ниже — использования с file:line и
/// сниппетом. Одного «file:line» мало: у многострочной декларации конец неизвестен,
/// а без заголовка неясно, к какому из одноимённых символов относится использование.
/// </summary>
[Tool("csharp_references",
    "Find all references (usages) of a C# symbol by name in the QwenPlayground solution, " +
    "grouped by the declaration they belong to. Each group header shows the declaration " +
    "(kind, signature, file:12-45 and the comment above it); each usage line shows " +
    "file:line and a code snippet. Semantic search — no false positives from text grep.",
    ToolGroup.CSharp)]
public sealed class CSharpReferencesTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    private const int MaxReferences = 50;
    private const int MaxGroups = 20;
    private const int MaxSnippetLength = 120;

    [ToolParameter("Symbol name to find references for, e.g. QwenChatTemplate or Render", Required = true)]
    public string Name { get; set; } = string.Empty;

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        // Два множества намеренно: ключ декларации («path:12-45») и ключ использования
        // («path:12») в одном множестве склеились бы на однострочных декларациях.
        var seenDeclarations = new HashSet<string>(StringComparer.Ordinal);
        var seenUsages = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<string>();
        var textCache = new Dictionary<DocumentId, SourceText>();
        var total = 0;
        var truncated = false;
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
                if (done)
                {
                    break;
                }
                // Перегрузка (ISymbol, Solution): ищем ссылки по всему солюшену,
                // в том числе в других проектах.
                var element = await LocationFormatter.ElementAsync(symbol, cancellationToken);
                if (element.File is null || !seenDeclarations.Add(element.Reference))
                {
                    // Один и тот же символ находится столько раз, сколько проектов
                    // ссылается на его проект — без дедупа группа дублируется.
                    continue;
                }
                var lines = new List<string>();
                var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
                foreach (var reference in references)
                {
                    foreach (var referenceLocation in reference.Locations)
                    {
                        var where = LocationFormatter.Usage(referenceLocation.Location);
                        if (where is null || !seenUsages.Add(where))
                        {
                            continue;
                        }
                        var snippet = await GetSnippetAsync(
                            referenceLocation.Document, textCache,
                            referenceLocation.Location.GetLineSpan().StartLinePosition.Line, cancellationToken);
                        lines.Add($"  {where}: {snippet}");
                        total++;
                        if (total >= MaxReferences || groups.Count >= MaxGroups)
                        {
                            truncated = true;
                            done = true;
                            break;
                        }
                    }
                    if (done)
                    {
                        break;
                    }
                }
                // Декларация без использований — тоже ответ на вопрос «где это используется».
                if (lines.Count == 0 && !done)
                {
                    lines.Add("  (no usages found)");
                }
                if (lines.Count > 0)
                {
                    groups.Add(LocationFormatter.Declaration(element, symbol) + "\n" + string.Join('\n', lines));
                }
            }
        }

        if (groups.Count == 0)
        {
            return $"no references to '{Name}' found";
        }
        var builder = new StringBuilder();
        builder.Append(total == 0
            ? $"declarations of '{Name}' ({groups.Count}), no usages:\n"
            : $"{total} reference(s) to '{Name}' in {groups.Count} declaration(s):\n");
        builder.Append(string.Join('\n', groups));
        if (truncated)
        {
            builder.Append($"\n... (truncated at {MaxReferences} references / {MaxGroups} declarations)");
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
