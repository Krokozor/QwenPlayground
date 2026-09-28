using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// Общий форматтер «чертежа» типа: заголовок типа и все члены с диапазонами строк и
/// первыми строками комментариев, включая вложенные типы (рекурсия). Используется
/// csharp_outline (по файлу) и csharp_class_map (по имени типа).
///
/// Диапазон вместо одной строки: по «:12» модель открывает файл и не знает, сколько
/// строк занимает метод — при многострочном теле это лишний вызов read_file наугад.
/// Комментарий отвечает на «что это вообще», не открывая файл.
/// </summary>
internal static class TypeMapFormatter
{
    public static void AppendType(StringBuilder builder, TypeDeclarationSyntax type, string indent)
    {
        var lines = LocationFormatter.Lines(type.GetLocation().GetLineSpan());
        builder.Append(indent).Append(type.Keyword.Text).Append(' ').Append(type.Identifier.Text)
               .Append(" :").Append(LocationFormatter.WithDoc(lines, LocationFormatter.Doc(type)))
               .Append('\n');

        var memberIndent = indent + "  ";
        foreach (var member in type.Members)
        {
            if (member is TypeDeclarationSyntax nested)
            {
                AppendType(builder, nested, memberIndent);
                continue;
            }

            var memberLines = LocationFormatter.Lines(member.GetLocation().GetLineSpan());
            var signature = member switch
            {
                MethodDeclarationSyntax method => $"method {method.Identifier.Text}({FormatParameters(method.ParameterList.Parameters)})",
                PropertyDeclarationSyntax property => $"property {property.Type} {property.Identifier.Text}",
                FieldDeclarationSyntax field => $"field {field.Declaration.Type} {field.Declaration.Variables.FirstOrDefault()?.Identifier.Text}",
                ConstructorDeclarationSyntax ctor => $"ctor ({FormatParameters(ctor.ParameterList.Parameters)})",
                _ => null
            };
            if (signature is not null)
            {
                builder.Append(memberIndent).Append(signature).Append(" :")
                    .Append(LocationFormatter.WithDoc(memberLines, LocationFormatter.Doc(member)))
                    .Append('\n');
            }
        }
    }

    private static string FormatParameters(IReadOnlyList<ParameterSyntax> parameters)
        => string.Join(", ", parameters.Select(p => p.Type?.ToString() ?? "?"));
}
