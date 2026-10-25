using AiProxy.Code;
using AiProxy.Core.Content.Code;

namespace AiProxy.Tests.Unit.Content.Code;

public sealed class JavaScriptCodeLexerTests
{
    private const string Hint = "[fixture: fetch original before editing]";
    private const string Padding = "// a long removable comment so the compact view plus its hint is smaller than the source\n";

    [Fact]
    public void Tokens_cover_the_exact_source_with_offsets_and_lines()
    {
        const string source = "#!/usr/bin/env node\r\n/** 😀 doc */\r\nconst é = /a\\/b/gi; // x\r\n\r\nlet e = <div a=\"1\">  hi {/* c */} &amp; </div>;\r\n";

        var result = new JavaScriptCodeLexer().Lex(source);

        LexedSource.AssertCovers(source, result);
        Assert.Equal(["/** 😀 doc */", "// x", "/* c */"], LexedSource.Texts(result, CodeTokenKind.Comment));
        Assert.Equal(3, LexedSource.Single(result, CodeTokenKind.Comment, "// x").Line);
        LexedSource.Single(result, CodeTokenKind.String, "/a\\/b/gi");
        LexedSource.Single(result, CodeTokenKind.String, "  hi ");
    }

    [Fact]
    public void Comment_markers_inside_strings_regexes_and_templates_are_strings()
    {
        const string source = "const a = '// x'; const b = \"/* y */\"; const c = `/* z */ ${a} // w`; const r = /\\/\\*q/;\n";

        var result = new JavaScriptCodeLexer().Lex(source);

        Assert.Empty(LexedSource.Texts(result, CodeTokenKind.Comment));
        Assert.Equal(["'// x'", "\"/* y */\"", "`/* z */ ", " // w`", "/\\/\\*q/"], LexedSource.Texts(result, CodeTokenKind.String));
    }

    [Fact]
    public void Nested_template_interpolations_are_code_and_their_text_is_string()
    {
        const string source = "const t = `a ${b + `c ${d /* n */}`} e`;\n";

        var result = new JavaScriptCodeLexer().Lex(source);

        LexedSource.AssertCovers(source, result);
        Assert.Equal(["t", "b", "d"], LexedSource.Texts(result, CodeTokenKind.Identifier));
        Assert.Equal(["`a ", "`c ", "`", " e`"], LexedSource.Texts(result, CodeTokenKind.String));
        Assert.Equal(["/* n */"], LexedSource.Texts(result, CodeTokenKind.Comment));
        Assert.DoesNotContain(result.Tokens, static token => token.Kind == CodeTokenKind.String && token.Text.Contains("${", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("function f( {\n")]
    [InlineData("let x = ;\n")]
    [InlineData("import d from 'm'\n(y)\n")]
    [InlineData("const \\u0061b = 1; ab++;\n")]
    public void Parse_errors_and_escaped_identifiers_are_unsupported_without_partial_tokens(string source)
    {
        var result = new JavaScriptCodeLexer().Lex(source);

        Assert.False(result.Supported);
        Assert.Empty(result.Tokens);
    }

    [Fact]
    public void Reducer_returns_the_exact_original_for_a_parse_error()
    {
        const string source = Padding + "function f( {\n";

        var result = new CodeReducer(new JavaScriptCodeLexer()).Reduce(source, "example.js", null, Hint);

        Assert.False(result.Changed);
        Assert.Equal(source, result.Text);
    }

    [Fact]
    public void Reducer_removes_comments_keeps_strings_and_original_line_numbers()
    {
        const string source = "1\t// a long removable comment so the compact view plus its hint is smaller than the source\n2\tconst s = '// kept';\n3\t/* dropped\n4\t   entirely */\n5\tconst t = `/* kept */`; // gone\n";

        var result = new CodeReducer(new JavaScriptCodeLexer()).Reduce(source, "example.js", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal($"2\tconst s = '// kept';\n5\tconst t = `/* kept */`;\n{Hint}", result.Text);
    }

    [Fact]
    public void Reducer_removes_bound_imports_but_keeps_side_effect_require_and_asi_hazard_imports()
    {
        const string source = Padding
            + "import a, { b as c } from 'm';\n"
            + "import * as ns from \"n\"\n"
            + "import 'side-effect';\n"
            + "const r = require('x');\n"
            + "export { q } from 'z';\n"
            + "let y = r\n"
            + "import d from 'hazard';\n"
            + "(y)\n";

        var result = new CodeReducer(new JavaScriptCodeLexer()).Reduce(source, "example.mjs", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal(
            "import 'side-effect';\n"
            + "const r = require('x');\n"
            + "export { q } from 'z';\n"
            + "let y = r\n"
            + "import d from 'hazard';\n"
            + $"(y)\n{Hint}",
            result.Text);
    }

    [Fact]
    public void Renaming_never_picks_a_keyword_or_renames_reserved_names()
    {
        var names = LexedSource.ManyNames(300).ToArray();
        var source = Padding
            + "function f() { return [this, arguments, this, arguments, undefined, undefined, console, console]; }\n"
            + string.Concat(names.Select(static name => $"let {name} = {name};\n"));

        var result = new CodeReducer(new JavaScriptCodeLexer()).Reduce(source, "example.js", new CodeReductionOptions(RenameIdentifiers: true), Hint);

        Assert.True(result.Changed);
        Assert.True(result.RenameMap.Count >= 280);
        Assert.DoesNotContain(result.RenameMap.Keys, static name => name is "as" or "do" or "if" or "in");
        Assert.Contains("at", result.RenameMap.Keys);
        Assert.Contains("[this, arguments, this, arguments, undefined, undefined, console, console]", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.js", true)]
    [InlineData("a.mjs", true)]
    [InlineData("a.cjs", true)]
    [InlineData("a.JSX", true)]
    [InlineData("a.ts", false)]
    [InlineData(null, false)]
    public void Supports_javascript_paths(string? path, bool expected) =>
        Assert.Equal(expected, new JavaScriptCodeLexer().SupportsPath(path));

    [Fact]
    public void Dialect_grammar_leaves_its_literal_map_untouched()
    {
        var strings = new Dictionary<string, string?> { ["template_literal_type"] = "template_type" };

        var grammar = JavaScriptCodeLexer.Script(["identifier"], strings);

        Assert.Equal(["template_literal_type"], strings.Keys);
        Assert.Equal("template_type", grammar.Strings["template_literal_type"]);
        Assert.Equal("template_substitution", grammar.Strings["template_string"]);
    }
}
