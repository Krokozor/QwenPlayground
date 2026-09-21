using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// Неиспользуемые using-директивы (IDE0005) через Roslyn. Зачем: IDE0005 — IDE-only
/// диагностика, в сборке не видна даже при включении в .editorconfig, а csharp_diagnostics
/// читает только compilation-диагностики. Алгоритм консервативный (без ложных срабатываний):
/// 1) собираем все символы, реально используемые в файле (IdentifierName → GetSymbolInfo);
/// 2) using считается неиспользуемым, если НИ ОДИН используемый символ не «живёт» в его
///    namespace (для using static — не является членом его типа). Всё, что отчитано,
///    действительно можно удалить; возможны пропуски (тип используется только qualified).
/// </summary>
[Tool("csharp_unused_usings",
    "Find unused using directives (IDE0005) across the solution via Roslyn — this diagnostic is IDE-only and invisible in the build. Returns file:line per unused using. Conservative: every reported using is safely removable; fully-qualified-only usages may be missed. Optional 'project' filter (name substring, e.g. 'Core').",
    ToolGroup.CSharp)]
public sealed class CSharpUnusedUsingsTool : AgentTool
{
    private static readonly RoslynService Service = RoslynService.Shared;

    [ToolParameter("Optional: only projects whose name contains this substring (e.g. 'Core').")]
    public string Project { get; set; } = "";

    public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var solution = await Service.GetSolutionAsync(cancellationToken);
        var results = new List<string>();

        foreach (var project in solution.Projects)
        {
            if (!string.IsNullOrEmpty(Project) &&
                !project.Name.Contains(Project, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            foreach (var tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Сгенерированный код (GlobalUsings.g.cs и пр.), obj/ и nuget-инжекты
                // (Test.Sdk.Program.cs) не трогаем.
                if (tree.FilePath is null || tree.FilePath.EndsWith(".g.cs") ||
                    tree.FilePath.Contains("\\obj\\") || tree.FilePath.Contains("/obj/") ||
                    tree.FilePath.Contains(".nuget\\"))
                {
                    continue;
                }

                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot(cancellationToken);
                var path = Path.GetRelativePath(SelfBuild.SelfBuildPaths.WorkspaceRoot, tree.FilePath);

                // 1) Все реально используемые символы файла (комментарии — trivia,
                //    в DescendantNodes не попадают). SymbolEqualityComparer: namespace-
                //    символы сравнивать по ссылке нельзя (merged-namespace из нескольких
                //    сборок — разные инстансы, например System.Windows в WPF).
                var used = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    var sym = model.GetSymbolInfo(id, cancellationToken).Symbol;
                    if (sym is not null)
                    {
                        used.Add(sym);
                    }
                }

                // 2) Каждая using-директива: используется?
                foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
                {
                    // global using (ImplicitUsings / GlobalUsings.g.cs) — отдельный механизм.
                    if (usingDirective.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.GlobalKeyword)))
                    {
                        continue;
                    }

                    var sym = model.GetSymbolInfo(usingDirective.Name, cancellationToken).Symbol;
                    if (sym is null)
                    {
                        continue; // не резолвится — про это скажет компилятор
                    }

                    bool isUsed;
                    if (usingDirective.UsingKeyword.IsKind(SyntaxKind.StaticKeyword))
                    {
                        // using static X; — живёт, если использован член типа X.
                        isUsed = sym is ITypeSymbol staticType &&
                                 used.Any(s => s.ContainingSymbol is not null &&
                                               SymbolEqualityComparer.Default.Equals(s.ContainingSymbol, staticType));
                    }
                    else if (sym is INamespaceSymbol ns)
                    {
                        // using Ns; — живёт, если использован символ, объявленный в Ns (или вложенном).
                        isUsed = used.Any(s => InNamespace(s, ns));
                    }
                    else if (sym is ITypeSymbol type)
                    {
                        // using Alias = A.B.C; — живёт, если алиас резолвился в используемый символ.
                        isUsed = used.Contains(type); // HashSet с SymbolEqualityComparer
                    }
                    else
                    {
                        isUsed = true; // не знаем — не трогаем
                    }

                    if (!isUsed)
                    {
                        var lineNum = usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        results.Add($"{path}:{lineNum}: using {usingDirective.Name.ToFullString()};");
                    }
                }
            }
        }

        if (results.Count == 0)
        {
            return "no unused usings found";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"unused usings: {results.Count}");
        foreach (var r in results.Take(150))
        {
            builder.AppendLine(r);
        }
        if (results.Count > 150)
        {
            builder.AppendLine($"... ({results.Count - 150} more)");
        }
        return builder.ToString();
    }

    /// <summary>
    /// Символ объявлен в namespace или во вложенном (обход цепочки вверх).
    /// Сравнение по полному имени: merged-namespace (один namespace в нескольких
    /// сборках, например System.Windows в WPF) даёт РАЗНЫЕ инстансы namespace-символов,
    /// и ни SymbolEqualityComparer, ни == их не сошлют.
    /// </summary>
    private static bool InNamespace(ISymbol symbol, INamespaceSymbol ns)
    {
        var target = ns.ToDisplayString();
        for (var n = symbol.ContainingNamespace;
             n is not null && !n.IsGlobalNamespace;
             n = n.ContainingNamespace)
        {
            if (n.ToDisplayString() == target)
            {
                return true;
            }
        }
        return false;
    }
}
