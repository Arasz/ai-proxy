using AiProxy.Code;
using AiProxy.Core.Content.Code;

namespace AiProxy.Tests.Unit.Content.Code;

public sealed class CSharpCodeLexerTests
{
    [Fact]
    public void Comment_markers_in_a_string_are_not_comments()
    {
        var result = new CSharpCodeLexer().Lex("var text = \"// literal\"; // actual\n");

        Assert.True(result.Supported);
        Assert.Contains(result.Tokens, static token => token is { Kind: CodeTokenKind.String, Text: "\"// literal\"" });
        Assert.Single(result.Tokens, static token => token.Kind == CodeTokenKind.Comment);
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
        Assert.Contains(result.Tokens, static token => token.Kind == CodeTokenKind.String);
        Assert.Single(result.Tokens, static token => token.Kind == CodeTokenKind.Comment);
        Assert.Equal(source, string.Concat(result.Tokens.Select(static token => token.Text)));
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

    [Fact]
    public void Active_preprocessor_directive_is_unsupported_without_partial_tokens()
    {
        var result = new CSharpCodeLexer().Lex("#region active\n#endregion\nclass C { }\n");

        Assert.False(result.Supported);
        Assert.Empty(result.Tokens);
    }

    [Theory]
    [InlineData("\"hello\"u8")]
    [InlineData("\"\"\"hello\"\"\"u8")]
    public void Utf8_string_suffix_stays_inside_a_string_token(string source)
    {
        var result = new CSharpCodeLexer().Lex($"var bytes = {source};");

        Assert.True(result.Supported);
        Assert.Contains(result.Tokens, token => token.Kind == CodeTokenKind.String && token.Text == source);
        Assert.DoesNotContain(result.Tokens, static token => token is { Kind: CodeTokenKind.Identifier, Text: "u8" });
    }

    [Fact]
    public void Nested_interpolation_expressions_are_lexed_as_code()
    {
        const string source = "var text = $\"Hello {Call($\"{item.Name}\")} {nameof(item)}\";";
        var result = new CSharpCodeLexer().Lex(source);

        Assert.True(result.Supported);
        Assert.Contains(result.Tokens, static token => token is { Kind: CodeTokenKind.Identifier, Text: "item" });
        Assert.Contains(result.Tokens, static token => token is { Kind: CodeTokenKind.Identifier, Text: "Name" });
        Assert.Contains(result.Tokens, static token => token is { Kind: CodeTokenKind.String, Text: "Hello " });
        Assert.Equal(source, string.Concat(result.Tokens.Select(static token => token.Text)));
    }

    [Fact]
    public void Escaped_and_unicode_identifiers_are_identifiers_but_keywords_are_not()
    {
        const string source = "int @café = 1; int value = café;";
        var lexer = new CSharpCodeLexer();
        var result = lexer.Lex(source);

        Assert.True(result.Supported);
        Assert.Equal(2, result.Tokens.Count(static token => token is { Kind: CodeTokenKind.Identifier, ValueText: "café" }));
        Assert.Contains(result.Tokens, static token => token is { Kind: CodeTokenKind.Identifier, Text: "@café" });
        Assert.Contains(result.Tokens, static token => token is { Kind: CodeTokenKind.Keyword, Text: "int" });
        Assert.True(lexer.IsKeyword("as"));
        Assert.True(lexer.IsKeyword("nint"));
        Assert.False(lexer.IsKeyword("café"));
    }

    [Fact]
    public void Mixed_CSharp_newlines_keep_exact_offsets_and_original_line_numbers()
    {
        const string source = "int first;\r\nint second;\u0085int third;\u2028int fourth;\u2029int fifth;";

        var result = new CSharpCodeLexer().Lex(source);

        Assert.True(result.Supported);
        Assert.Equal(source, string.Concat(result.Tokens.Select(static token => token.Text)));
        var identifiers = result.Tokens.Where(static token => token.Kind == CodeTokenKind.Identifier).ToDictionary(static token => token.Text);
        Assert.Equal(1, identifiers["first"].Line);
        Assert.Equal(2, identifiers["second"].Line);
        Assert.Equal(3, identifiers["third"].Line);
        Assert.Equal(4, identifiers["fourth"].Line);
        Assert.Equal(5, identifiers["fifth"].Line);
        foreach (var token in identifiers.Values) Assert.Equal(token.Text, source.Substring(token.Offset, token.Text.Length));
    }

    [Fact]
    public void Contextual_words_that_parse_as_identifiers_keep_identifier_value_text()
    {
        const string source = "class C { void M() { int @record=1; @record++; System.Console.WriteLine(record); } }";

        var lexer = new CSharpCodeLexer();
        var result = lexer.Lex(source);

        Assert.True(result.Supported);
        Assert.Equal(3, result.Tokens.Count(static token => token is { Kind: CodeTokenKind.Identifier, ValueText: "record" }));
        Assert.True(lexer.IsKeyword("record"));
        Assert.True(lexer.IsKeyword("value"));
    }

    [Fact]
    public void Documentation_comment_trivia_preserves_source_span_and_indentation()
    {
        const string source = "  /// <summary>Long documentation.</summary>\r\n  class C { }";

        var result = new CSharpCodeLexer().Lex(source);

        Assert.True(result.Supported);
        Assert.Equal(source, string.Concat(result.Tokens.Select(static token => token.Text)));
        var comment = Assert.Single(result.Tokens, static token => token.Kind == CodeTokenKind.Comment);
        Assert.Equal(source.IndexOf("///", StringComparison.Ordinal), comment.Offset);
        Assert.Equal(1, comment.Line);
        var declaration = Assert.Single(result.Tokens, static token => token is { Kind: CodeTokenKind.Keyword, Text: "class" });
        Assert.Equal(2, declaration.Line);
        Assert.Equal("class", source.Substring(declaration.Offset, declaration.Text.Length));
    }
}