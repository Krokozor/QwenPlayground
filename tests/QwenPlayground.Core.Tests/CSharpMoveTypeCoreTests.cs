using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using QwenPlayground.Core.Roslyn;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// CSharpMoveTypeCore — ядро переноса типа (файл/namespace). Тесты на in-memory
/// AdhocWorkspace: живое солюшен не трогаем (move в тестах — деструктивная
/// операция). Покрыто: простой перенос + using в ссылках, переписывание
/// квалификаторов OldNs.Type, сохранение соседей в старом файле, отказ от
/// вложенного типа, отказ при коллизии в целевом namespace.
/// </summary>
public sealed class CSharpMoveTypeCoreTests
{
    private static (AdhocWorkspace Workspace, Solution Solution) CreateWorkspace(params (string Name, string Source)[] files)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Test");
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", "C#");
        foreach (var (name, source) in files)
        {
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId, name), name, SourceText.From(source));
        }
        // BCL-референсы: без них int/object дают CS0518-шум и диагностика
        // до/после move становится недостоверной.
        var coreLib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var systemRuntime = MetadataReference.CreateFromFile(
            Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll"));
        solution = solution
            .AddMetadataReference(projectId, coreLib)
            .AddMetadataReference(projectId, systemRuntime);
        workspace.TryApplyChanges(solution);
        return (workspace, solution);
    }

    private static Document? FindDocument(Solution solution, string name)
    {
        // In-memory документ: FilePath пуст, имя — в Name.
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (string.Equals(document.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return document;
                }
            }
        }
        return null;
    }

    private static async Task<string> DocumentTextAsync(Solution solution, string name)
    {
        var document = FindDocument(solution, name);
        Assert.NotNull(document);
        var text = await document!.GetTextAsync();
        return text.ToString();
    }

    [Fact]
    public async Task Move_Simple_AddsUsingToReferencingFile()
    {
        const string a = "namespace Ns1\n{\n    public class Foo\n    {\n        public int Bar { get; set; }\n    }\n}";
        const string b = "using Ns1;\n\npublic class Consumer\n{\n    public Foo Make() { return new Foo(); }\n}";
        var (_, solution) = CreateWorkspace(("A.cs", a), ("B.cs", b));

        var outcome = await CSharpMoveTypeCore.ApplyAsync(solution, "Foo", null, "C.cs", "Ns2", CancellationToken.None);

        Assert.Null(outcome.Error);
        var textA = await DocumentTextAsync(outcome.Modified!, "A.cs");
        var textC = await DocumentTextAsync(outcome.Modified!, "C.cs");
        var textB = await DocumentTextAsync(outcome.Modified!, "B.cs");
        Assert.DoesNotContain("class Foo", textA);
        Assert.Contains("namespace Ns2", textC);
        Assert.Contains("public class Foo", textC);
        Assert.Contains("using Ns2;", textB); // неквалифицированная ссылка — using добавлен
    }

    [Fact]
    public async Task Move_QualifiedReferences_RewritesNamespacePrefix()
    {
        const string a = "namespace Ns1\n{\n    public class Foo\n    {\n    }\n}";
        const string b = "public class Consumer\n{\n    public Ns1.Foo Make() { return new Ns1.Foo(); }\n}";
        var (_, solution) = CreateWorkspace(("A.cs", a), ("B.cs", b));

        var outcome = await CSharpMoveTypeCore.ApplyAsync(solution, "Foo", null, "C.cs", "Ns2", CancellationToken.None);

        Assert.Null(outcome.Error);
        var textB = await DocumentTextAsync(outcome.Modified!, "B.cs");
        Assert.Contains("Ns2.Foo Make()", textB);
        Assert.Contains("new Ns2.Foo()", textB);
        Assert.DoesNotContain("Ns1.Foo", textB);
    }

    [Fact]
    public async Task Move_OldFileKeepsOtherTypes()
    {
        const string a = "namespace Ns1\n{\n    public class Foo\n    {\n    }\n\n    public class Baz\n    {\n        public Foo F { get; set; } = null!;\n    }\n}";
        var (_, solution) = CreateWorkspace(("A.cs", a));

        var outcome = await CSharpMoveTypeCore.ApplyAsync(solution, "Foo", null, "C.cs", "Ns2", CancellationToken.None);

        Assert.Null(outcome.Error);
        var textA = await DocumentTextAsync(outcome.Modified!, "A.cs");
        Assert.Contains("class Baz", textA); // сосед остался
        Assert.DoesNotContain("class Foo", textA);
        Assert.Contains("using Ns2;", textA); // Baz ссылается на Foo — using добавлен
    }

    [Fact]
    public async Task Move_NestedType_Rejected()
    {
        const string a = "namespace Ns1\n{\n    public class Outer\n    {\n        public class Foo { }\n    }\n}";
        var (_, solution) = CreateWorkspace(("A.cs", a));

        var outcome = await CSharpMoveTypeCore.ApplyAsync(solution, "Foo", null, "C.cs", "Ns2", CancellationToken.None);

        Assert.NotNull(outcome.Error);
        Assert.Contains("nested", outcome.Error);
    }

    [Fact]
    public async Task Move_PartialType_Rejected()
    {
        const string a = "namespace Ns1\n{\n    public partial class Foo\n    {\n    }\n}";
        var (_, solution) = CreateWorkspace(("A.cs", a));

        var outcome = await CSharpMoveTypeCore.ApplyAsync(solution, "Foo", null, "C.cs", "Ns2", CancellationToken.None);

        Assert.NotNull(outcome.Error);
        Assert.Contains("partial", outcome.Error);
    }

    [Fact]
    public async Task Move_CollisionInTargetNamespace_RejectedByCompiler()
    {
        const string a = "namespace Ns1\n{\n    public class Foo\n    {\n    }\n}";
        const string d = "namespace Ns2\n{\n    public class Foo\n    {\n    }\n}";
        var (_, solution) = CreateWorkspace(("A.cs", a), ("D.cs", d));

        // Два Foo — disambiguation по файлу, потом коллизия в целевом namespace.
        var outcome = await CSharpMoveTypeCore.ApplyAsync(solution, "Foo", "A.cs", "C.cs", "Ns2", CancellationToken.None);

        Assert.NotNull(outcome.Error);
        Assert.Contains("Move rejected", outcome.Error);
    }

    [Fact]
    public async Task Move_SameFileAndNamespace_NothingToDo()
    {
        const string a = "namespace Ns1\n{\n    public class Foo\n    {\n    }\n}";
        var (_, solution) = CreateWorkspace(("A.cs", a));

        var outcome = await CSharpMoveTypeCore.ApplyAsync(solution, "Foo", null, "A.cs", "Ns1", CancellationToken.None);

        Assert.NotNull(outcome.Error);
        Assert.Contains("nothing to do", outcome.Error);
    }

    [Fact]
    public async Task Move_NotFound_ReturnsError()
    {
        const string a = "namespace Ns1\n{\n    public class Foo\n    {\n    }\n}";
        var (_, solution) = CreateWorkspace(("A.cs", a));

        var outcome = await CSharpMoveTypeCore.ApplyAsync(solution, "NoSuchType", null, "C.cs", "Ns2", CancellationToken.None);

        Assert.NotNull(outcome.Error);
        Assert.Contains("not found", outcome.Error);
    }
}
