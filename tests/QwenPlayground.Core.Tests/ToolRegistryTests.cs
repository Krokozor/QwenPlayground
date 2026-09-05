using System.Text.Json.Nodes;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.Core.Tests;

public sealed class ToolRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly ToolRegistry _registry = new();
    private readonly ToolContext _context;

    public ToolRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qwen_playground_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _context = new ToolContext(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Definitions_ContainsBuiltinTools()
    {
        var names = _registry.Definitions.Select(d => d.Name).ToList();

        Assert.Contains("read_file", names);
        Assert.Contains("write_file", names);
        Assert.Contains("edit_file", names);
        Assert.Contains("glob", names);
        Assert.Contains("grep", names);
        Assert.Contains("shell", names);
    }

    [Fact]
    public void Definitions_GeneratesSchemaWithSnakeCaseParameters()
    {
        var shell = _registry.Definitions.Single(d => d.Name == "shell");
        var properties = shell.Parameters["properties"]!.AsObject();

        Assert.True(properties.ContainsKey("command"));
        Assert.True(properties.ContainsKey("timeout_seconds"));
        Assert.Equal("integer", properties["timeout_seconds"]!["type"]!.GetValue<string>());
        Assert.Contains("command", shell.Parameters["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        var write = await _registry.ExecuteAsync("write_file",
            new JsonObject { ["path"] = "sub/test.txt", ["content"] = "hello world" }, _context);
        Assert.Contains("wrote", write);

        var read = await _registry.ExecuteAsync("read_file", new JsonObject { ["path"] = "sub/test.txt" }, _context);
        Assert.Contains("hello world", read);
        Assert.Contains("1: hello world", read);
    }

    [Fact]
    public async Task EditFile_ReplacesExactMatch()
    {
        await _registry.ExecuteAsync("write_file",
            new JsonObject { ["path"] = "a.txt", ["content"] = "foo bar foo" }, _context);

        var ambiguous = await _registry.ExecuteAsync("edit_file",
            new JsonObject { ["path"] = "a.txt", ["old_string"] = "foo", ["new_string"] = "baz" }, _context);
        Assert.Contains("matches 2 times", ambiguous);

        var ok = await _registry.ExecuteAsync("edit_file",
            new JsonObject { ["path"] = "a.txt", ["old_string"] = "bar foo", ["new_string"] = "baz" }, _context);
        Assert.Contains("edited", ok);
        Assert.Equal("foo baz", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Glob_FindsFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.cs"), "");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "");

        var result = await _registry.ExecuteAsync("glob", new JsonObject { ["pattern"] = "**/*.cs" }, _context);

        Assert.Contains("src/a.cs", result.Replace('\\', '/'));
        Assert.DoesNotContain("b.txt", result);
    }

    [Fact]
    public async Task Grep_FindsMatchingLines()
    {
        File.WriteAllText(Path.Combine(_root, "g.txt"), "alpha\nbeta\ngamma");

        var result = await _registry.ExecuteAsync("grep", new JsonObject { ["pattern"] = "et" }, _context);

        Assert.Contains("g.txt:2: beta", result.Replace('\\', '/'));
    }

    [Fact]
    public async Task Shell_RunsCommand()
    {
        var result = await _registry.ExecuteAsync("shell",
            new JsonObject { ["command"] = "echo hi", ["timeout_seconds"] = "10" }, _context);

        Assert.Contains("exit code: 0", result);
        Assert.Contains("hi", result);
    }

    [Fact]
    public async Task UnknownTool_ReturnsError()
    {
        var result = await _registry.ExecuteAsync("nope", new JsonObject(), _context);

        Assert.Equal("Error: unknown tool 'nope'", result);
    }

    [Fact]
    public async Task MissingRequiredParameter_ReturnsError()
    {
        var result = await _registry.ExecuteAsync("read_file", new JsonObject(), _context);

        Assert.Contains("missing required parameter 'path'", result);
    }

    [Fact]
    public async Task PathEscape_IsBlocked()
    {
        var result = await _registry.ExecuteAsync("read_file",
            new JsonObject { ["path"] = "../../outside.txt" }, _context);

        Assert.Contains("escapes allowed roots", result);
    }

    [Fact]
    public async Task Collections_ArrayParams_ParsedFromJsonStrings()
    {
        var registry = new ToolRegistry(typeof(ToolRegistryTests).Assembly);

        var result = await registry.ExecuteAsync("collections_probe",
            new JsonObject
            {
                ["files"] = "[\"a.txt\",\"b.txt\"]",
                ["counts"] = "[1,2,3]",
                ["mapping"] = "{\"k\":\"v\"}",
                ["flag"] = "True"
            }, _context);

        Assert.Equal("files=a.txt|b.txt;counts=1|2|3;map=k=v;flag=True", result);
    }

    [Fact]
    public async Task Collections_SingleValue_FallsBackToSingleElement()
    {
        var registry = new ToolRegistry(typeof(ToolRegistryTests).Assembly);

        var result = await registry.ExecuteAsync("collections_probe",
            new JsonObject { ["files"] = "only.txt" }, _context);

        Assert.Contains("files=only.txt", result);
        Assert.Contains("counts=;map=;flag=False", result);
    }
}

[Tool("collections_probe", "test tool with collection parameters")]
public sealed class CollectionsProbeTool : AgentTool
{
    [ToolParameter("files")]
    public string[] Files { get; set; } = Array.Empty<string>();

    [ToolParameter("counts")]
    public List<int> Counts { get; set; } = new();

    [ToolParameter("mapping")]
    public Dictionary<string, string> Mapping { get; set; } = new();

    [ToolParameter("flag")]
    public bool Flag { get; set; }

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(
            $"files={string.Join('|', Files)};counts={string.Join('|', Counts)};" +
            $"map={string.Join('|', Mapping.Select(p => p.Key + "=" + p.Value))};flag={Flag}");
}
