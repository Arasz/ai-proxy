using AiProxy.Code;
using AiProxy.Core.Content.Code;
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

    public static TheoryData<string, string> UnloadableLexers => new()
    {
        { "python", "a.py" },
        { "javascript", "a.js" },
        { "typescript", "a.ts" },
        { "tsx", "a.tsx" },
    };

    [Theory]
    [MemberData(nameof(UnloadableLexers))]
    public void A_grammar_that_fails_to_load_makes_the_lexer_unsupported_and_the_reducer_return_the_original(string lexer, string path)
    {
        var missing = new TreeSitterLanguage("NoSuchGrammar");
        ICodeLexer subject = lexer switch
        {
            "python" => new PythonCodeLexer(missing),
            "javascript" => new JavaScriptCodeLexer(missing),
            _ => new TypeScriptCodeLexer(missing, missing),
        };
        const string source = "// a long removable comment so the compact view plus its hint is smaller than the source\nx = 1\n";

        var lexed = subject.Lex(source, path);
        var reduced = new CodeReducer(subject).Reduce(source, path, null, "[hint]");

        Assert.True(subject.SupportsPath(path));
        Assert.False(lexed.Supported);
        Assert.Empty(lexed.Tokens);
        Assert.False(reduced.Changed);
        Assert.Equal(source, reduced.Text);
        Assert.Null(missing.Value);
    }
}
