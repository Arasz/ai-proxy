using AiProxy.Core.Content.Code;

namespace AiProxy.Tests.Unit.Content.Code;

public sealed class CSharpCodeLexerTests
{
    [Fact]
    public void Comment_markers_in_a_string_are_not_comments()
    {
        var result = new CSharpCodeLexer().Lex("var text = \"// literal\"; // actual\n");

        Assert.True(result.Supported);
        Assert.Contains(result.Tokens, token => token.Kind == CodeTokenKind.String && token.Text == "\"// literal\"");
        Assert.Single(result.Tokens, token => token.Kind == CodeTokenKind.Comment);
    }
    [Theory]
    [InlineData("var text = @\"/* literal */\"; // actual")]
    [InlineData("var text = \"\"\"\n// literal\n\"\"\"; // actual")]
    [InlineData("var text = $\"// {name}\"; // actual")]
    [InlineData("var text = '\"'; // actual")]
    public void Literal_forms_keep_their_comment_like_bytes(string source)
    {
        var result = new CSharpCodeLexer().Lex(source);

        Assert.True(result.Supported);
        Assert.Single(result.Tokens, token => token.Kind == CodeTokenKind.String);
        Assert.Single(result.Tokens, token => token.Kind == CodeTokenKind.Comment);
        Assert.Equal(source, string.Concat(result.Tokens.Select(token => token.Text)));
    }
    [Theory]
    [InlineData("/* never closed")]
    [InlineData("var x = \"never closed")]
    [InlineData("var x = $\"{name\";")]
    [InlineData("var x = $\"{Call(\"nested\")}\";")]
    [InlineData("var x = $\"\"\"raw {name}\"\"\";")]
    [InlineData("#if DEBUG\nclass C {}\n#endif")]
    [InlineData("int \\u0061 = 1;")]
    [InlineData("int café = 1;")]
    [InlineData("var x = @identifier;")]
    [InlineData("char x = 'ab';")]
    [InlineData("var x = \"\\q\";")]
    public void Ambiguous_or_unmodelled_syntax_has_no_partial_tokens(string source)
    {
        var result = new CSharpCodeLexer().Lex(source);

        Assert.False(result.Supported);
        Assert.Empty(result.Tokens);
    }
}
