using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using QwenPlayground.Core.Roslyn;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// CSharpRenameCore — ядро безопасного rename (антидот regex-замен). Тесты на
/// in-memory AdhocWorkspace: живое солюшен не трогаем (rename в тестах —
/// деструктивная операция). Покрыто: массовая замена по документам, коллизия
/// (ловит компилятор), ключевое слово, неоднозначность + disambiguation по файлу,
/// rename типа со ссылками.
/// </summary>
public sealed class CSharpRenameCoreTests
{
    private const string SourceA =
        "public class Foo\n{\n    public int Bar { get; set; }\n}";

    private const string SourceB =
        "public class Consumer\n{\n    public int Use(Foo f) { return f.Bar; }\n}";

    private static (AdhocWorkspace Workspace, Solution Solution) CreateWorkspace(string sourceA, string sourceB)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Test");
        // Порядок аргументов: (projectId, name, assemblyName, languageName) —
        // язык на ЧЕТВЁРТОМ месте («C#» на третьем даёт мёртвый workspace).
        // BCL-референсы: без них int/object дают CS0518-шум и диагностика
        // до/после rename становится недостоверной.
        var coreLib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var systemRuntime = MetadataReference.CreateFromFile(
            Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll"));
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", "C#")
            .AddDocument(DocumentId.CreateNewId(projectId, "A.cs"), "A.cs", SourceText.From(sourceA))
            .AddDocument(DocumentId.CreateNewId(projectId, "B.cs"), "B.cs", SourceText.From(sourceB))
            .AddMetadataReference(projectId, coreLib)
            .AddMetadataReference(projectId, systemRuntime);
        workspace.TryApplyChanges(solution);
        return (workspace, solution);
    }

    private static Document? FindDocument(Solution solution, string filePath)
    {
        // In-memory документ: FilePath пуст, имя — в Name (проверено пробником).
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (string.Equals(document.Name, filePath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(document.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return document;
                }
            }
        }
        return null;
    }

    private static async Task<string> DocumentTextAsync(Solution solution, string path)
    {
        var document = FindDocument(solution, path);
        Assert.NotNull(document);
        var text = await document!.GetTextAsync();
        return text.ToString();
    }

    [Fact]
    public async Task Rename_MemberAcrossDocuments_UpdatesAllReferences()
    {
        var (_, solution) = CreateWorkspace(SourceA, SourceB);

        var outcome = await CSharpRenameCore.ApplyAsync(solution, "Bar", "Baz", null, null, CancellationToken.None);

        Assert.Null(outcome.Error);
        Assert.Equal(2, outcome.TotalLocations); // декларация (A) + использование (B)
        var textA = await DocumentTextAsync(outcome.Modified!, "A.cs");
        var textB = await DocumentTextAsync(outcome.Modified!, "B.cs");
        Assert.Contains("public int Baz { get; set; }", textA);
        Assert.Contains("return f.Baz;", textB);
        Assert.DoesNotContain("Bar", textA);
        Assert.DoesNotContain("Bar", textB);
    }

    [Fact]
    public async Task Rename_Collision_RejectedByCompiler_NothingChanged()
    {
        const string withCollision =
            "public class Foo\n{\n    public int Bar { get; set; }\n    public int Baz { get; set; }\n}";
        var (_, solution) = CreateWorkspace(withCollision, SourceB);

        var outcome = await CSharpRenameCore.ApplyAsync(solution, "Bar", "Baz", null, null, CancellationToken.None);

        Assert.NotNull(outcome.Error);
        Assert.Contains("collides", outcome.Error);
        Assert.Contains("CS0102", outcome.Error);
        Assert.Null(outcome.ChangedFiles);
    }

    [Fact]
    public async Task Rename_ToKeyword_Rejected()
    {
        var (_, solution) = CreateWorkspace(SourceA, SourceB);

        var outcome = await CSharpRenameCore.ApplyAsync(solution, "Bar", "class", null, null, CancellationToken.None);

        Assert.NotNull(outcome.Error);
        Assert.Contains("keyword", outcome.Error);
    }

    [Fact]
    public async Task Rename_Ambiguous_ListsCandidates_NothingChanged()
    {
        // Два одноимённых метода в РАЗНЫХ файлах.
        const string mInA = "public class Foo\n{\n    public int M() { return 1; }\n}";
        const string mInB = "public class Other\n{\n    public int M() { return 2; }\n}";
        var (_, solution) = CreateWorkspace(mInA, mInB);

        var outcome = await CSharpRenameCore.ApplyAsync(solution, "M", "N", null, null, CancellationToken.None);

        Assert.NotNull(outcome.Error);
        Assert.Contains("Ambiguous", outcome.Error);
        Assert.Equal(2, outcome.Candidates!.Count);
        Assert.Null(outcome.ChangedFiles);
    }

    [Fact]
    public async Task Rename_Ambiguous_WithFileDisambiguates()
    {
        // file="A.cs" выбирает метод из A.cs; метод из B.cs не тронут.
        const string mInA = "public class Foo\n{\n    public int M() { return 1; }\n}";
        const string mInB = "public class Other\n{\n    public int M() { return 2; }\n}";
        var (_, solution) = CreateWorkspace(mInA, mInB);

        var outcome = await CSharpRenameCore.ApplyAsync(solution, "M", "N", "A.cs", null, CancellationToken.None);

        Assert.Null(outcome.Error);
        var textA = await DocumentTextAsync(outcome.Modified!, "A.cs");
        var textB = await DocumentTextAsync(outcome.Modified!, "B.cs");
        Assert.Contains("public int N() { return 1; }", textA);
        Assert.Contains("public int M() { return 2; }", textB); // B.cs не тронут
    }

    [Fact]
    public async Task Rename_Type_UpdatesReferences()
    {
        const string typeA =
            "namespace Ns\n{\n    public class Foo\n    {\n        public int Bar { get; set; }\n    }\n}";
        const string typeB =
            "using Ns;\npublic class Consumer\n{\n    public Foo Make() { return new Foo(); }\n}";
        var (_, solution) = CreateWorkspace(typeA, typeB);

        var outcome = await CSharpRenameCore.ApplyAsync(solution, "Foo", "Baz", null, null, CancellationToken.None);

        Assert.Null(outcome.Error);
        var textB = await DocumentTextAsync(outcome.Modified!, "B.cs");
        Assert.Contains("public Baz Make() { return new Baz(); }", textB);
        Assert.Contains("using Ns;", textB); // namespace не менялся — using цел
    }

    [Fact]
    public async Task Rename_NotFound_ReturnsError()
    {
        var (_, solution) = CreateWorkspace(SourceA, SourceB);

        var outcome = await CSharpRenameCore.ApplyAsync(solution, "NoSuchSymbol", "X", null, null, CancellationToken.None);

        Assert.NotNull(outcome.Error);
        Assert.Contains("not found", outcome.Error);
    }
}
