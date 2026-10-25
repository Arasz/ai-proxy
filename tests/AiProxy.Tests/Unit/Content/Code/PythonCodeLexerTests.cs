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

    [Fact]
    public void Reducer_keeps_directive_comments_verbatim_on_their_original_lines()
    {
        const string source = "1\t#!/usr/bin/env python\n"
            + "2\t# -*- coding: utf-8 -*-\n"
            + "3\t# a long removable comment so the compact view plus its hint is smaller than the source\n"
            + "4\timport os\n"
            + "5\tx = os.path  # type: ignore\n"
            + "6\ty = 1  # gone\n";

        var result = new CodeReducer(new PythonCodeLexer()).Reduce(source, "example.py", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal(
            "1\t#!/usr/bin/env python\n2\t# -*- coding: utf-8 -*-\n5\tx = os.path  # type: ignore\n6\ty = 1\n" + Hint,
            result.Text);
    }

    [Theory]
    [InlineData("x = 1  # type: int", 1, CodeTokenKind.Other)]
    [InlineData("x = 1  # noqa", 1, CodeTokenKind.Other)]
    [InlineData("x = 1  # long line # noqa: E501", 1, CodeTokenKind.Other)]
    [InlineData("x = 1  # pragma: no cover", 1, CodeTokenKind.Other)]
    [InlineData("x = 1  # pylint: disable=invalid-name", 1, CodeTokenKind.Other)]
    [InlineData("# mypy: ignore-errors", 1, CodeTokenKind.Other)]
    [InlineData("# fmt: off", 1, CodeTokenKind.Other)]
    [InlineData("# fmt: on", 1, CodeTokenKind.Other)]
    [InlineData("x = 1  # fmt: skip", 1, CodeTokenKind.Other)]
    [InlineData("x = f()  # pyright: ignore[reportCallIssue]", 1, CodeTokenKind.Other)]
    [InlineData("# ruff: noqa: E501", 1, CodeTokenKind.Other)]
    [InlineData("import a  # isort: skip", 1, CodeTokenKind.Other)]
    [InlineData("x = eval(s)  # nosec", 1, CodeTokenKind.Other)]
    [InlineData("x = f()  # pyre-ignore[16]", 1, CodeTokenKind.Other)]
    [InlineData("#!/usr/bin/env python3", 1, CodeTokenKind.Other)]
    [InlineData("# vim: set fileencoding=utf-8 :", 2, CodeTokenKind.Other)]
    [InlineData("# -*- coding: latin-1 -*-", 3, CodeTokenKind.Comment)]
    [InlineData("#! not a shebang off line one", 2, CodeTokenKind.Comment)]
    [InlineData("x = 1  # the type of x", 1, CodeTokenKind.Comment)]
    public void Comments_a_tool_reads_are_kept_as_code(string line, int lineNumber, CodeTokenKind expected)
    {
        var source = string.Concat(Enumerable.Repeat("y = 0\n", lineNumber - 1)) + line + "\n";

        var result = new PythonCodeLexer().Lex(source);

        LexedSource.AssertCovers(source, result);
        var comment = Assert.Single(result.Tokens, static token => token.Text.StartsWith('#'));
        Assert.Equal(expected, comment.Kind);
        Assert.Equal(lineNumber, comment.Line);
    }

    [Fact]
    public void Renaming_never_picks_a_name_spelled_by_a_string()
    {
        const string source = Padding + "alpha_name = 1\nprint(alpha_name, getattr(C, \"a\"), getattr(C, 'b'))\n";

        var result = new CodeReducer(new PythonCodeLexer()).Reduce(source, "example.py", new CodeReductionOptions(RenameIdentifiers: true), Hint);

        Assert.True(result.Changed);
        Assert.Equal("alpha_name", result.RenameMap["c"]);
        Assert.Contains("getattr(C, \"a\"), getattr(C, 'b')", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("example.py", true)]
    [InlineData("EXAMPLE.PY", true)]
    [InlineData("example.pyi", false)]
    [InlineData(null, false)]
    public void Supports_python_paths(string? path, bool expected) =>
        Assert.Equal(expected, new PythonCodeLexer().SupportsPath(path));

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Line_numbers_advance_by_every_line_break_inside_a_multi_line_token(string eol)
    {
        var source = "doc = \"\"\"first\nsecond\nthird\"\"\"\nafter = 1\n".Replace("\n", eol, StringComparison.Ordinal);

        var result = new PythonCodeLexer().Lex(source);

        LexedSource.AssertCovers(source, result);
        Assert.Equal(4, LexedSource.Single(result, CodeTokenKind.Identifier, "after").Line);
    }

    [Fact]
    public void Reducer_keeps_an_import_that_shares_its_line_with_code()
    {
        const string source = Padding + "x = 1; import os\ny = os\n";

        var result = new CodeReducer(new PythonCodeLexer()).Reduce(source, "example.py", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal($"x = 1; import os\ny = os\n{Hint}", result.Text);
    }

    [Fact]
    public void Renaming_treats_spellings_that_normalize_alike_as_one_name()
    {
        var names = LexedSource.ManyNames(40).ToArray();
        var source = Padding
            + "ﬁle = 1\nfile = ﬁle + file\n"
            + string.Concat(names.Select(static name => $"{name} = {name}\n"));

        var result = new CodeReducer(new PythonCodeLexer()).Reduce(source, "example.py", new CodeReductionOptions(RenameIdentifiers: true), Hint);

        Assert.True(result.Changed);
        var shortName = Assert.Single(result.RenameMap, static pair => pair.Value == "file").Key;
        Assert.DoesNotContain("ﬁ", result.Text, StringComparison.Ordinal);
        Assert.Contains($"{shortName} = 1\n{shortName} = {shortName} + {shortName}\n", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_init_keeps_its_imports_because_they_are_its_exports()
    {
        const string source = "from .models import User\nfrom .views import render\n\n__all__ = [\"User\", \"render\"]\n";

        Assert.Equal(2, new PythonCodeLexer().Lex(source, "pkg/models.py").Imports?.Count);
        Assert.Empty(new PythonCodeLexer().Lex(source, "pkg/__init__.py").Imports ?? [new CodeSpan(0, 1)]);
    }
}
