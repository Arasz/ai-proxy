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
        Assert.Contains(result.Tokens, token => token.Kind == CodeTokenKind.String);
        Assert.Single(result.Tokens, token => token.Kind == CodeTokenKind.Comment);
        Assert.Equal(source, string.Concat(result.Tokens.Select(token => token.Text)));
    }
    [Theory]
    [InlineData("/* never closed")]
    [InlineData("var x = \"never closed")]
    [InlineData("var x = $\"{name\";")]
    [InlineData("#if DEBUG\nclass C {}\n#endif")]
    [InlineData("char x = 'ab';")]
    [InlineData("var x = \"\\q\";")]
    public void Ambiguous_or_unmodelled_syntax_has_no_partial_tokens(string source)
    {
        var result = new CSharpCodeLexer().Lex(source);

        Assert.False(result.Supported);
        Assert.Empty(result.Tokens);
    }

    [Theory]
    [InlineData("\"hello\"u8")]
    [InlineData("\"\"\"hello\"\"\"u8")]
    public void Utf8_string_suffix_stays_inside_a_string_token(string source)
    {
        var result = new CSharpCodeLexer().Lex("var bytes = " + source + ";");

        Assert.True(result.Supported);
        Assert.Contains(result.Tokens, token => token.Kind == CodeTokenKind.String && token.Text == source);
        Assert.DoesNotContain(result.Tokens, token => token.Kind == CodeTokenKind.Identifier && token.Text == "u8");
    }

    [Fact]
    public void Nested_interpolation_expressions_are_lexed_as_code()
    {
        const string source = "var text = $\"Hello {Call($\"{item.Name}\")} {nameof(item)}\";";
        var result = new CSharpCodeLexer().Lex(source);

        Assert.True(result.Supported);
        Assert.Contains(result.Tokens, token => token.Kind == CodeTokenKind.Identifier && token.Text == "item");
        Assert.Contains(result.Tokens, token => token.Kind == CodeTokenKind.Identifier && token.Text == "Name");
        Assert.Contains(result.Tokens, token => token.Kind == CodeTokenKind.String && token.Text == "Hello ");
        Assert.Equal(source, string.Concat(result.Tokens.Select(token => token.Text)));
    }

    [Fact]
    public void Escaped_and_unicode_identifiers_are_identifiers_but_keywords_are_not()
    {
        const string source = "int @café = 1; int value = café;";
        var result = new CSharpCodeLexer().Lex(source);

        Assert.True(result.Supported);
        Assert.Equal(2, result.Tokens.Count(token => token.Kind == CodeTokenKind.Identifier && token.ValueText == "café"));
        Assert.Contains(result.Tokens, token => token.Kind == CodeTokenKind.Identifier && token.Text == "@café");
        Assert.Contains(result.Tokens, token => token.Kind == CodeTokenKind.Keyword && token.Text == "int");
        Assert.True(CSharpCodeLexer.IsKeyword("as"));
        Assert.True(CSharpCodeLexer.IsKeyword("nint"));
        Assert.False(CSharpCodeLexer.IsKeyword("café"));
    }
}
