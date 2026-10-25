using TreeSitter;

namespace AiProxy.Tests.Unit.Content.Code;

public sealed class TreeSitterLoadingTests
{
    [Theory]
    [InlineData("Python", "x = 1\n", "module")]
    [InlineData("JavaScript", "let x = 1;\n", "program")]
    [InlineData("TypeScript", "let x: number = 1;\n", "program")]
    [InlineData("Tsx", "const e = <div>{x}</div>;\n", "program")]
    public void Native_grammar_loads_and_parses_without_errors(string name, string source, string root)
    {
        using var language = new Language(name);
        using var parser = new Parser(language);
        using var tree = parser.Parse(source)!;

        Assert.Equal(root, tree.RootNode.Type);
        Assert.False(tree.RootNode.HasError);
    }
}
