using AiProxy.Code;
using AiProxy.Core.Content.Code;

namespace AiProxy.Tests.Unit.Content.Code;

public sealed class PythonCodeLexerTests
{
    private const string Hint = "[fixture: fetch original before editing]";
    private const string Padding = "# a long removable comment so the compact view plus its hint is smaller than the source\n";

    [Fact]
    public void Tokens_cover_the_exact_source_with_offsets_and_lines()
    {
        const string source = "#!/usr/bin/env python\r\n\"\"\"Module 😀 doc.\"\"\"\r\nimport os\r\n\r\ndef f(é, *args):  # trailing\r\n    x = 1 + \\\r\n        2\r\n    return f\"{é!r:>{x}} 😀\"\r\n";

        var result = new PythonCodeLexer().Lex(source);

        LexedSource.AssertCovers(source, result);
        Assert.Contains(result.Tokens, static token => token.Kind == CodeTokenKind.NewLine && token.Text == "\r\n");
        Assert.Contains(result.Tokens, static token => token.Kind == CodeTokenKind.Whitespace && token.Text == "    ");
        Assert.Equal(5, LexedSource.Single(new PythonCodeLexer().Lex(source), CodeTokenKind.Comment, "# trailing").Line);
    }

    [Fact]
    public void Comment_markers_inside_strings_and_docstrings_are_strings()
    {
        const string source = "def f():\n    \"\"\"Docstring # not a comment.\"\"\"\n    return \"# not a comment\"  # real\n";

        var result = new PythonCodeLexer().Lex(source);

        Assert.Equal(["# real"], LexedSource.Texts(result, CodeTokenKind.Comment));
        LexedSource.Single(result, CodeTokenKind.String, "\"\"\"Docstring # not a comment.\"\"\"");
        LexedSource.Single(result, CodeTokenKind.String, "\"# not a comment\"");
    }

    [Fact]
    public void Fstring_literal_parts_are_strings_and_interpolations_are_code()
    {
        const string source = "s = f'''a {d[\"k\"]!r:>{width}} # b''' + f\"{t['q']}\"\n";

        var result = new PythonCodeLexer().Lex(source);

        LexedSource.AssertCovers(source, result);
        Assert.Empty(LexedSource.Texts(result, CodeTokenKind.Comment));
        Assert.Equal(["s", "d", "width", "t"], LexedSource.Texts(result, CodeTokenKind.Identifier));
        Assert.Equal(["f'''a ", "\"k\"", ":>", " # b'''", "f\"", "'q'", "\""], LexedSource.Texts(result, CodeTokenKind.String));
    }

    [Theory]
    [InlineData("def f(:\n    pass\n")]
    [InlineData("x = (1\n")]
    [InlineData("print(f\"{value=}\")\n")]
    public void Parse_errors_and_debug_fstrings_are_unsupported_without_partial_tokens(string source)
    {
        var result = new PythonCodeLexer().Lex(source);

        Assert.False(result.Supported);
        Assert.Empty(result.Tokens);
    }

    [Fact]
    public void Reducer_returns_the_exact_original_for_a_parse_error()
    {
        const string source = Padding + "def f(:\n    pass\n";

        var result = new CodeReducer(new PythonCodeLexer()).Reduce(source, "example.py", null, Hint);

        Assert.False(result.Changed);
        Assert.False(result.Supported);
        Assert.Equal(source, result.Text);
    }

    [Fact]
    public void Reducer_removes_comments_keeps_strings_and_original_line_numbers()
    {
        const string source = "1\t# a long removable comment so the compact view plus its hint is smaller than the source\n2\tdef f():\n3\t    \"\"\"Doc # kept.\"\"\"\n4\t    # dropped entirely\n5\t    return '# kept'  # gone\n";

        var result = new CodeReducer(new PythonCodeLexer()).Reduce(source, "example.py", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal($"2\tdef f():\n3\t    \"\"\"Doc # kept.\"\"\"\n5\t    return '# kept'\n{Hint}", result.Text);
    }

    [Fact]
    public void Reducer_removes_module_imports_but_keeps_future_nested_and_shared_line_imports()
    {
        const string source = Padding
            + "from __future__ import annotations\n"
            + "import os, sys\n"
            + "from a.b import (\n    c,\n    d,\n)\n"
            + "from . import e  # why\n"
            + "import g; h = 1\n"
            + "try:\n    import json\nexcept ImportError:\n    json = None\n"
            + "x = os\n";

        var result = new CodeReducer(new PythonCodeLexer()).Reduce(source, "example.py", new CodeReductionOptions(RemoveComments: true), Hint);

        Assert.True(result.Changed);
        Assert.Equal(
            "from __future__ import annotations\n"
            + "import g; h = 1\n"
            + "try:\n    import json\nexcept ImportError:\n    json = None\n"
            + $"x = os\n{Hint}",
            result.Text);
    }

    [Fact]
    public void Renaming_never_picks_a_keyword_or_renames_reserved_names()
    {
        var names = LexedSource.ManyNames(300).ToArray();
        var source = Padding
            + "class K:\n    def m(self, match, case):\n        print(self, match, case, type, len)\n        return self\n"
            + string.Concat(names.Select(static name => $"{name} = {name}\n"));

        var result = new CodeReducer(new PythonCodeLexer()).Reduce(source, "example.py", new CodeReductionOptions(RenameIdentifiers: true), Hint);

        Assert.True(result.Changed);
        Assert.True(result.RenameMap.Count >= 280);
        Assert.DoesNotContain(result.RenameMap.Keys, static name => name is "as" or "if" or "in" or "is");
        Assert.Contains("at", result.RenameMap.Keys);
        Assert.DoesNotContain("self", result.RenameMap.Values);
        Assert.DoesNotContain("match", result.RenameMap.Values);
        Assert.DoesNotContain("print", result.RenameMap.Values);
        Assert.Contains("print(self, match, case, type, len)", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("example.py", true)]
    [InlineData("EXAMPLE.PY", true)]
    [InlineData("example.pyi", false)]
    [InlineData(null, false)]
    public void Supports_python_paths(string? path, bool expected) =>
        Assert.Equal(expected, new PythonCodeLexer().SupportsPath(path));
}
