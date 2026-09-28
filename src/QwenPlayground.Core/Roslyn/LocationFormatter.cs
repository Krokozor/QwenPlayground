using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// Общий форматтер координат и документации для C#-тулов: «файл:12» для однострочного
/// элемента, «файл:12-45» для многострочного и первая строка комментария над декларацией.
///
/// Зачем: одно «file:line» у многнострочного типа/метода почти бесполезно — модель
/// открывает файл, читает строку начала и не знает, где кончается элемент и сколько
/// кода пропустила. Диапазон строк + описание («что это вообще») превращают координату
/// в рабочую ссылку.
///
/// Две грабли Roslyn, из-за которых берём ИСХОДНЫЙ УЗЕЛ, а не Location символа:
/// · <c>symbol.Locations[..].GetLineSpan()</c> у метода/типа даёт диапазон только
///   строки ИДЕНТИФИКАТОРА (проверено: GetSolutionAsync — 39-39, хотя тело до 69) —
///   для «где кончается» бесполезно;
/// · <c>DeclaringSyntaxReference.GetSyntax()</c> отдаёт узел, спарсенный из одного спана:
///   ведущей тривы (а значит и комментария) в нём нет — только перевод строки.
///   Полная трива есть только у узла из корня документа, поэтому берём
///   <c>root.FindNode(DeclaringSyntaxReference.Span)</c>.
///
/// Формат строки: `kind signature — path:12-45  // doc`.
/// </summary>
internal static class LocationFormatter
{
    /// <summary>Кап описания: длинный многоабзацный XML-doc одной строкой — шум.</summary>
    private const int MaxDocChars = 200;

    /// <summary>Элемент: где объявлен (путь + диапазон строк) и что это (комментарий).</summary>
    public readonly record struct Element(string? File, string Lines, string? Doc)
    {
        public string Reference => File is null ? Lines : $"{File}:{Lines}";
    }

    /// <summary>
    /// Координаты декларации символа по исходному узлу документа. Fallback — Location
    /// символа, если исходного узла нет (тогда Lines — строка идентификатора).
    /// </summary>
    public static async Task<Element> ElementAsync(ISymbol symbol, CancellationToken cancellationToken = default)
    {
        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is not null)
        {
            var root = await reference.SyntaxTree.GetRootAsync(cancellationToken);
            var node = root.FindNode(reference.Span, getInnermostNodeForTie: false);
            // Спан во весь файл/неймспейс: FindNode отдаёт CompilationUnit — описание
            // элемента из него бесполезно, берём родителя токена по началу спана.
            if (node is null or CompilationUnitSyntax)
            {
                node = root.FindToken(reference.Span.Start).Parent;
            }
            if (node is not null)
            {
                var span = node.GetLocation().GetLineSpan();
                return new Element(
                    string.IsNullOrEmpty(span.Path) ? null : Relative(span.Path),
                    Lines(span),
                    Doc(node));
            }
        }

        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null)
        {
            return new Element(null, "—", null);
        }
        var fallback = location.GetLineSpan();
        return new Element(
            string.IsNullOrEmpty(fallback.Path) ? null : Relative(fallback.Path),
            Lines(fallback),
            null);
    }

    /// <summary>Описание декларации символа одной строкой: `kind signature — path:12-45  // doc`.</summary>
    public static async Task<string> DeclarationAsync(ISymbol symbol, CancellationToken cancellationToken = default)
        => Declaration(await ElementAsync(symbol, cancellationToken), symbol);

    /// <summary>То же по уже вычисленным координатам (декларация не ищется дважды).</summary>
    public static string Declaration(Element element, ISymbol symbol)
    {
        var head = element.File is null
            ? $"{Kind(symbol)} {symbol.ToDisplayString()} — (no source location)"
            : $"{Kind(symbol)} {symbol.ToDisplayString()} — {element.Reference}";
        return WithDoc(head, element.Doc);
    }

    /// <summary>Только описание символа (без координат) — для строк «кто вызывает».</summary>
    public static async Task<string?> DocAsync(ISymbol symbol, CancellationToken cancellationToken = default)
        => (await ElementAsync(symbol, cancellationToken)).Doc;

    /// <summary>
    /// Позиция ИСПОЛЬЗОВАНИЯ: «path:12». Без диапазона намеренно — ссылка это точка
    /// (идентификатор), а не многострочный элемент; «12-45» тут было бы враньём.
    /// </summary>
    public static string? Usage(Location? location)
    {
        if (location is null || !location.IsInSource)
        {
            return null;
        }
        var span = location.GetLineSpan();
        return string.IsNullOrEmpty(span.Path)
            ? null
            : $"{Relative(span.Path)}:{span.StartLinePosition.Line + 1}";
    }

    /// <summary>Диапазон строк узла: «12» или «12-45».</summary>
    public static string Lines(FileLinePositionSpan span)
    {
        var start = span.StartLinePosition.Line + 1;
        var end = span.EndLinePosition.Line + 1;
        return end <= start ? $"{start}" : $"{start}-{end}";
    }

    /// <summary>
    /// Первая непустая строка комментария над узлом: XML-doc («///», «/** */») или
    /// обычный «//». Берётся только первая строка — назначение, а не пересказ.
    /// </summary>
    public static string? Doc(SyntaxNode? node)
    {
        if (node is null)
        {
            return null;
        }
        foreach (var trivia in node.GetLeadingTrivia())
        {
            var raw = trivia switch
            {
                // В одном trivia может быть несколько строк «///» подряд.
                SyntaxTrivia t when t.IsKind(SyntaxKind.SingleLineCommentTrivia)
                    || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) => t.ToString(),
                SyntaxTrivia t when t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                    || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia) => t.ToString(),
                _ => null,
            };
            if (raw is null)
            {
                continue;
            }
            foreach (var line in raw.Split('\n'))
            {
                var text = CleanDocLine(line);
                if (text is not null)
                {
                    return text;
                }
            }
        }
        return null;
    }

    /// <summary>Приклеивает описание к готовой строке: `… — path:12  // doc`.</summary>
    public static string WithDoc(string head, string? doc) =>
        doc is null || head.Length == 0 ? head : $"{head}  // {doc}";

    private static string Kind(ISymbol symbol) => symbol.Kind.ToString().ToLowerInvariant();

    private static string Relative(string path) => Path.GetRelativePath(SelfBuildPaths.WorkspaceRoot, path);

    /// <summary>
    /// Комментарий → текст описания: снимаем маркеры («///», «//», «/*», «*/») и теги
    /// doc-разметки, схлопываем пробелы. Строка только из разметки («&lt;summary&gt;»)
    /// описанием не считается — иначе в выдачу попадает мусор.
    ///
    /// Отдельно отсекаем «разделители» вида «// ─────────── members ───────────»:
    /// они ведут секцию, а не описывают элемент, но формально подходят под комментарий.
    /// Признак — непропорционально много не-букв (буквы/цифры составляют меньше
    /// половины строки) или их меньше трёх.
    /// </summary>
    private static string? CleanDocLine(string line)
    {
        var text = line.Trim();
        foreach (var marker in new[] { "///", "//", "/**", "/*", "*/" })
        {
            if (text.StartsWith(marker, StringComparison.Ordinal))
            {
                text = text[marker.Length..].TrimStart();
                break;
            }
        }
        foreach (var tag in new[] { "<summary>", "</summary>", "<para>", "</para>" })
        {
            text = text.Replace(tag, string.Empty, StringComparison.Ordinal);
        }
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0 || !IsDescription(text))
        {
            return null;
        }
        return text.Length > MaxDocChars ? text[..MaxDocChars] + "…" : text;
    }

    private static bool IsDescription(string text)
    {
        var meaningful = 0;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                meaningful++;
            }
        }
        return meaningful >= 3 && meaningful * 2 >= text.Length;
    }
}
