using AiProxy.Code;
using AiProxy.Core.Content.Code;

namespace AiProxy.Tests.Unit.Content.Code;

public sealed class TypeScriptCodeLexerTests
{
    private const string Hint = "[fixture: fetch original before editing]";
    private const string Padding = "// a long removable comment so the compact view plus its hint is smaller than the source\n";

    [Fact]
    public void Tokens_cover_the_exact_source_with_offsets_and_lines()
    {
        const string source = "/** 😀 */\r\ntype S = `a${string} // b`;\r\nlet v: number | undefined = this.a as any; // c\r\n";

        var result = new TypeScriptCodeLexer().Lex(source, "a.ts");

        LexedSource.AssertCovers(source, result);
        Assert.Equal(["/** 😀 */", "// c"], LexedSource.Texts(result, CodeTokenKind.Comment));
        Assert.Equal(["`a", " // b`"], LexedSource.Texts(result, CodeTokenKind.String));
        Assert.Equal(3, LexedSource.Single(result, CodeTokenKind.Comment, "// c").Line);
        Assert.Contains(result.Tokens, static token => token.Kind == CodeTokenKind.Keyword && token.Text == "string");
    }

    [Fact]
    public void Tsx_paths_use_the_tsx_grammar_and_ts_paths_do_not()
    {
        const string source = "const e = <div className=\"x\">  {/* c */} hi </div>;\n";

        var tsx = new TypeScriptCodeLexer().Lex(source, "a.tsx");
        var ts = new TypeScriptCodeLexer().Lex(source, "a.ts");

        LexedSource.AssertCovers(source, tsx);
        Assert.Equal(["/* c */"], LexedSource.Texts(tsx, CodeTokenKind.Comment));
        Assert.Equal(["\"x\"", "  ", " hi "], LexedSource.Texts(tsx, CodeTokenKind.String));
        Assert.False(ts.Supported);
    }

    [Fact]
    public void Comment_markers_inside_strings_and_templates_are_strings()
    {
        const string source = "const a: string = '// x'; const c = `/* z */ ${a as string} // w`;\n";

        var result = new TypeScriptCodeLexer().Lex(source, "a.ts");

        Assert.Empty(LexedSource.Texts(result, CodeTokenKind.Comment));
        Assert.Equal(["'// x'", "`/* z */ ", " // w`"], LexedSource.Texts(result, CodeTokenKind.String));
    }

    [Theory]
    [InlineData("let x: = 1;\n")]
    [InlineData("interface I { a: string\n")]
    public void Parse_errors_are_unsupported_and_the_reducer_returns_the_original(string body)
    {
        var source = Padding + body;

        var lexed = new TypeScriptCodeLexer().Lex(source, "a.ts");
        var reduced = new CodeReducer(new TypeScriptCodeLexer()).Reduce(source, "a.ts", null, Hint);

        Assert.False(lexed.Supported);
        Assert.Empty(lexed.Tokens);
        Assert.False(reduced.Changed);
        Assert.Equal(source, reduced.Text);
    }

    [Fact]
    public void Reducer_removes_type_and_value_imports_but_keeps_import_require()
    {
        const string source = "1\t// a long removable comment so the compact view plus its hint is smaller than the source\n"
            + "2\timport type { T } from 't';\n"
            + "3\timport { u } from './u';\n"
            + "4\timport x = require('y');\n"
            + "5\timport './side';\n"
            + "6\tlet v: T = u(x); // gone\n";

        var result = new CodeReducer(new TypeScriptCodeLexer()).Reduce(source, "a.ts", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal($"4\timport x = require('y');\n5\timport './side';\n6\tlet v: T = u(x);\n{Hint}", result.Text);
    }

    [Fact]
    public void Renaming_never_picks_a_keyword_or_renames_type_keywords()
    {
        var names = LexedSource.ManyNames(300).ToArray();
        var source = Padding
            + "type Q = Partial<Record<string, number>>; let p: Partial<Q> = {}; let r: Record<string, Q> = {};\n"
            + string.Concat(names.Select(static name => $"let {name}: number = {name};\n"));

        var result = new CodeReducer(new TypeScriptCodeLexer()).Reduce(source, "a.ts", new CodeReductionOptions(RenameIdentifiers: true), Hint);

        Assert.True(result.Changed);
        Assert.True(result.RenameMap.Count >= 280);
        Assert.DoesNotContain(result.RenameMap.Keys, static name => name is "as" or "do" or "if" or "in" or "is");
        Assert.Contains("at", result.RenameMap.Keys);
        Assert.DoesNotContain("Partial", result.RenameMap.Values);
        Assert.DoesNotContain("Record", result.RenameMap.Values);
        Assert.Contains("Partial<Record<string, number>>", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reducer_keeps_directive_comments_verbatim_on_their_original_lines()
    {
        const string source = "1\t/// <reference types=\"node\" />\n"
            + "2\t// a long removable comment so the compact view plus its hint is smaller than the source\n"
            + "3\t// @ts-ignore\n"
            + "4\tlet a: number = f();\n"
            + "5\t// @ts-expect-error\n"
            + "6\tlet b: string = 1; // gone\n";

        var result = new CodeReducer(new TypeScriptCodeLexer()).Reduce(source, "a.ts", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal(
            "1\t/// <reference types=\"node\" />\n3\t// @ts-ignore\n4\tlet a: number = f();\n5\t// @ts-expect-error\n6\tlet b: string = 1;\n" + Hint,
            result.Text);
    }

    [Theory]
    [InlineData("/// <reference path=\"a.d.ts\" />", CodeTokenKind.Other)]
    [InlineData("/// <amd-module name=\"m\" />", CodeTokenKind.Other)]
    [InlineData("// @ts-ignore", CodeTokenKind.Other)]
    [InlineData("// @ts-expect-error: why", CodeTokenKind.Other)]
    [InlineData("// @ts-nocheck", CodeTokenKind.Other)]
    [InlineData("// @ts-check", CodeTokenKind.Other)]
    [InlineData("/* eslint-disable no-console */", CodeTokenKind.Other)]
    [InlineData("// eslint-disable-next-line no-undef", CodeTokenKind.Other)]
    [InlineData("/* eslint-enable */", CodeTokenKind.Other)]
    [InlineData("/*! MIT license */", CodeTokenKind.Other)]
    [InlineData("//# sourceMappingURL=a.js.map", CodeTokenKind.Other)]
    [InlineData("/** @jsx h */", CodeTokenKind.Other)]
    [InlineData("/* @jsxImportSource preact */", CodeTokenKind.Other)]
    [InlineData("/// plain triple slash", CodeTokenKind.Comment)]
    [InlineData("// ts-ignore without the at sign", CodeTokenKind.Comment)]
    [InlineData("/* a license, but not a bang comment */", CodeTokenKind.Comment)]
    public void Comments_a_tool_reads_are_kept_as_code(string comment, CodeTokenKind expected)
    {
        var source = comment + "\nlet x = 1;\n";

        var result = new TypeScriptCodeLexer().Lex(source, "a.ts");

        LexedSource.AssertCovers(source, result);
        Assert.Equal(expected, result.Tokens[0].Kind);
        Assert.Equal(comment, result.Tokens[0].Text);
    }

    [Theory]
    [InlineData("a.ts", true)]
    [InlineData("a.TSX", true)]
    [InlineData("a.d.ts", true)]
    [InlineData("a.js", false)]
    [InlineData(null, false)]
    public void Supports_typescript_paths(string? path, bool expected) =>
        Assert.Equal(expected, new TypeScriptCodeLexer().SupportsPath(path));
}
