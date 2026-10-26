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
        const string source = $"{Padding}function f( {{\n";

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
        const string source = $"{Padding}import a, {{ b as c }} from 'm';\nimport * as ns from \"n\"\nimport 'side-effect';\nconst r = require('x');\nexport {{ q }} from 'z';\nlet y = r\nimport d from 'hazard';\n(y)\n";

        var result = new CodeReducer(new JavaScriptCodeLexer()).Reduce(source, "example.mjs", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal(
            $"import 'side-effect';\nconst r = require('x');\nexport {{ q }} from 'z';\nlet y = r\nimport d from 'hazard';\n(y)\n{Hint}",
            result.Text);
    }

    [Fact]
    public void Renaming_never_picks_a_keyword_or_renames_reserved_names()
    {
        var names = LexedSource.ManyNames(300).ToArray();
        var source = $"{Padding}function f() {{ return [this, arguments, this, arguments, undefined, undefined, console, console]; }}\n{string.Concat(names.Select(static name => $"let {name} = {name};\n"))}";

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

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Line_numbers_advance_by_every_line_break_inside_a_multi_line_token(string eol)
    {
        var source = "/* one\ntwo\nthree */\nafter;\nconst s = `a\nb\nc`;\nlast;\n".Replace("\n", eol, StringComparison.Ordinal);

        var result = new JavaScriptCodeLexer().Lex(source);

        LexedSource.AssertCovers(source, result);
        Assert.Equal(4, LexedSource.Single(result, CodeTokenKind.Identifier, "after").Line);
        Assert.Equal(8, LexedSource.Single(result, CodeTokenKind.Identifier, "last").Line);
    }

    [Fact]
    public void Renaming_never_picks_the_spelling_of_a_jsx_tag_or_attribute()
    {
        var names = LexedSource.ManyNames(60).ToArray();
        var source = $"{Padding}const e = <a b=\"1\">text</a>;\n{string.Concat(names.Select(static name => $"let {name} = {name};\n"))}";

        var result = new CodeReducer(new JavaScriptCodeLexer()).Reduce(source, "example.jsx", new CodeReductionOptions(RenameIdentifiers: true), Hint);

        Assert.True(result.Changed);
        Assert.True(result.RenameMap.Count >= 50);
        Assert.DoesNotContain(result.RenameMap.Keys, static name => name is "a" or "b");
        Assert.Contains("<a b=\"1\">text</a>", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Renaming_never_touches_jsx_namespace_names_used_repeatedly()
    {
        const string jsx = "<div namespace_prefix:attribute_label=\"1\"><i namespace_prefix:attribute_label=\"2\" /></div>";
        const string source = $"{Padding}const repeated_value = 1, other_value = repeated_value + repeated_value;\nexport const e = {jsx};\n";

        var result = new CodeReducer(new JavaScriptCodeLexer()).Reduce(source, "example.jsx", new CodeReductionOptions(RenameIdentifiers: true), Hint);

        Assert.True(result.Changed);
        Assert.Contains("repeated_value", result.RenameMap.Values);
        Assert.DoesNotContain(result.RenameMap.Values, static name => name is "namespace_prefix" or "attribute_label");
        Assert.Contains(jsx, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reducer_keeps_an_import_that_shares_its_line_with_code()
    {
        const string source = $"{Padding}a(); import b from 'm';\nb();\n";

        var result = new CodeReducer(new JavaScriptCodeLexer()).Reduce(source, "example.js", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal($"a(); import b from 'm';\nb();\n{Hint}", result.Text);
    }

    [Fact]
    public void Reducer_does_not_blank_a_using_shaped_statement_when_no_import_is_removable()
    {
        const string source = $"{Padding}using x = a.b;\nlet y = x;\n";

        var result = new CodeReducer(new JavaScriptCodeLexer()).Reduce(source, "example.js", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal($"using x = a.b;\nlet y = x;\n{Hint}", result.Text);
    }

    [Fact]
    public void Html_style_comment_lines_are_comments()
    {
        const string source = "<!-- legacy note\nlet x = 1;\n";

        var result = new JavaScriptCodeLexer().Lex(source);

        LexedSource.AssertCovers(source, result);
        Assert.Equal(["<!-- legacy note"], LexedSource.Texts(result, CodeTokenKind.Comment));
    }

    [Theory]
    [InlineData("// @ts-ignore", CodeTokenKind.Other)]
    [InlineData("/*! MIT */", CodeTokenKind.Other)]
    [InlineData("/* eslint-disable no-console */", CodeTokenKind.Other)]
    [InlineData("// prettier-ignore", CodeTokenKind.Other)]
    [InlineData("// biome-ignore lint/style: reason", CodeTokenKind.Other)]
    [InlineData("/* istanbul ignore next */", CodeTokenKind.Other)]
    [InlineData("/* c8 ignore next */", CodeTokenKind.Other)]
    [InlineData("// ts-ignore without the at sign", CodeTokenKind.Comment)]
    [InlineData("/* a license, but not a bang comment */", CodeTokenKind.Comment)]
    public void Comments_a_tool_reads_are_kept_as_code(string comment, CodeTokenKind expected)
    {
        var source = $"{comment}\nlet x = 1;\n";

        var result = new JavaScriptCodeLexer().Lex(source);

        LexedSource.AssertCovers(source, result);
        Assert.Equal(expected, result.Tokens[0].Kind);
        Assert.Equal(comment, result.Tokens[0].Text);
    }
}
