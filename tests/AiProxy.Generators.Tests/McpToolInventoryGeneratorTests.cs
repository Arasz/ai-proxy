using AiProxy.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
namespace AiProxy.Generators.Tests;
public sealed class McpToolInventoryGeneratorTests
{
    [Fact]
    public void Inventory_contains_every_declared_tool_name()
    {
        var result = Generate("Name = \"retrieve_original\", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false");
        Assert.Contains("retrieve_original", Assert.Single(result.GeneratedTrees).ToString());
        Assert.Empty(result.Diagnostics);
    }
    [Theory]
    [InlineData("Name", "APGEN001")]
    [InlineData("ReadOnly", "APGEN002")]
    [InlineData("Destructive", "APGEN002")]
    [InlineData("Idempotent", "APGEN002")]
    [InlineData("OpenWorld", "APGEN002")]
    public void Missing_explicit_contract_argument_is_a_compile_error(string omitted, string diagnostic)
    {
        string[] arguments = ["Name = \"retrieve_original\"", "ReadOnly = true", "Destructive = false", "Idempotent = true", "OpenWorld = false"];
        var result = Generate(string.Join(", ", arguments.Where(argument => !argument.StartsWith($"{omitted} =", StringComparison.Ordinal))));
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(diagnostic, error.Id);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    [Fact]
    public void Adding_a_tool_extends_the_inventory_without_a_second_list()
    {
        var result = Generate("Name = \"retrieve_original\", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false", true);
        var generated = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("retrieve_original", generated);
        Assert.Contains("second_tool", generated);
        Assert.Empty(result.Diagnostics);
    }

    private static GeneratorDriverRunResult Generate(string arguments, bool extraTool = false)
    {
        var source = """
            namespace ModelContextProtocol.Server {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class McpServerToolAttribute : System.Attribute {
                public string Name { get; set; }
                public bool ReadOnly { get; set; }
                public bool Destructive { get; set; }
                public bool Idempotent { get; set; }
                public bool OpenWorld { get; set; }
            }}
            public sealed class Tools {
                [ModelContextProtocol.Server.McpServerTool(ARGUMENTS)]
                public void Retrieve() { }
            }
            """.Replace("ARGUMENTS", arguments);
        if (extraTool)
        {
            source += "public sealed class MoreTools { [ModelContextProtocol.Server.McpServerTool(Name = \"second_tool\", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)] public void Second() {} }";
        }
        var compilation = CSharpCompilation.Create("Fixture", [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return CSharpGeneratorDriver.Create(new McpToolInventoryGenerator()).RunGenerators(compilation).GetRunResult();
    }
}
