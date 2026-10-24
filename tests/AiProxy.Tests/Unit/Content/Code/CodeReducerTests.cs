using AiProxy.Core.Content.Code;
using AiProxy.Code;

namespace AiProxy.Tests.Unit.Content.Code;

public sealed class CodeReducerTests
{
    private const string Hint = "[fixture: fetch original before editing]";

    [Fact]
    public void Comments_and_blank_lines_are_removed_but_indentation_survives()
    {
        const string source = "// A deliberately long comment makes the complete compacted view smaller than the original.\n\n    int value = 1; // another long removable comment\n";

        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", new CodeReductionOptions(), Hint);

        Assert.True(result.Changed);
        Assert.True(result.Supported);
        Assert.Equal($"    int value = 1;\n{Hint}", result.Text);
        Assert.Empty(result.RenameMap);
    }
    [Theory]
    [InlineData(null, "Example.cs")]
    [InlineData("", "Example.cs")]
    [InlineData("   ", "Example.cs")]
    [InlineData(Hint, null)]
    [InlineData(Hint, "Example.ts")]
    public void Missing_hint_or_unknown_path_returns_exact_original(string? hint, string? path)
    {
        const string source = "// long removable comment would otherwise allow reduction of this file\r\n    int value = 1;\r\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, path, null, hint);

        Assert.False(result.Changed);
        Assert.Equal(source, result.Text);
    }

    [Fact]
    public void Multiline_literals_keep_blank_lines_and_trailing_spaces()
    {
        const string source = "// a very long removable comment provides enough savings for the complete hint too\nvar text = \"\"\"\n    first   \n\n    last  \n    \"\"\";\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal($"var text = \"\"\"\n    first   \n\n    last  \n    \"\"\";\n{Hint}", result.Text);
    }

    [Fact]
    public void Interpolation_format_whitespace_is_preserved()
    {
        const string format = "var text = $@\"{number:000   \n\n}\";\n";
        var source = $"// a very long comment that makes the complete hinted view smaller than the original source\nint number = 1;\n{format}";

        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", null, Hint);

        Assert.True(result.Changed);
        Assert.Contains(format, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Contextual_and_native_integer_names_are_not_renamed_partially()
    {
        var source = "// a very long comment makes room for the hint while preserving all names\nclass @record { }\nclass C { record first; record second; public int @record { get; set; } void P() { record = 1; record++; } void U() { int \\u0072ecord = 0; \\u0072ecord++; } }\nclass D { void record() {} void M() { record(); record(); } }\nclass E { int @nint; void M() { @nint++; nint++; } }\nclass V { public int P { set { int @value = 0; @value++; this.P = value; value = @value; } } }\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", new CodeReductionOptions(RenameIdentifiers: true), Hint);

        Assert.True(result.Changed);
        Assert.DoesNotContain("record", result.RenameMap.Values);
        Assert.DoesNotContain("nint", result.RenameMap.Values);
        Assert.DoesNotContain("value", result.RenameMap.Values);
        Assert.Contains("class @record", result.Text, StringComparison.Ordinal);
        Assert.Contains("record first;", result.Text, StringComparison.Ordinal);
        Assert.Contains("record second;", result.Text, StringComparison.Ordinal);
        Assert.Contains("int \\u0072ecord = 0; \\u0072ecord++;", result.Text, StringComparison.Ordinal);
        Assert.Contains("public int @record", result.Text, StringComparison.Ordinal);
        Assert.Contains("record = 1; record++;", result.Text, StringComparison.Ordinal);
        Assert.Contains("void record()", result.Text, StringComparison.Ordinal);
        Assert.Contains("record(); record();", result.Text, StringComparison.Ordinal);
        Assert.Contains("int @nint;", result.Text, StringComparison.Ordinal);
        Assert.Contains("@nint++; nint++;", result.Text, StringComparison.Ordinal);
        Assert.Contains("int @value = 0; @value++; this.P = value; value = @value;", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Active_preprocessor_directive_returns_exact_original()
    {
        const string source = "#region active\n#endregion\n// long comment that otherwise would be removed from the compact view\nclass C { }\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", null, Hint);

        Assert.False(result.Changed);
        Assert.False(result.Supported);
        Assert.Equal(source, result.Text);
    }

    [Fact]
    public void Inline_comment_removal_keeps_identifier_separation()
    {
        const string source = "// a very long removable comment provides enough savings for the complete hint too\nint/*removed*/value = 1;\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", null, Hint);

        Assert.True(result.Changed);
        Assert.Contains("int           value", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_numbered_CRLF_input_is_byte_exact()
    {
        const string source = "  1\t// a long comment that must remain untouched\r\n  2\t#if DEBUG\r\n  3\t    int value = 1;\r\n  4\t#endif\r\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", null, Hint);

        Assert.False(result.Changed);
        Assert.False(result.Supported);
        Assert.Equal(source, result.Text);
    }

    [Fact]
    public void Equal_size_including_hint_returns_original()
    {
        const string source = "// removed\nint x;\n";
        var hint = new string('h', "// removed\n".Length);
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", null, hint);

        Assert.False(result.Changed);
        Assert.Equal(source, result.Text);
    }
    [Fact]
    public void Every_number_prefix_survives_removable_gaps_and_CRLF()
    {
        const string source = "  60\t// a very long removable comment provides enough savings for the complete hint too\r\n  61\t\r\n  62\t    int value = 1;\r\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal($"  62\t    int value = 1;\r\n{Hint}", result.Text);
    }
    [Fact]
    public void Only_complete_using_directives_are_removed()
    {
        const string source = "global using System.Text;\nusing static System.Math;\nusing Alias = System.Collections.Generic;\nusing\n    System.Threading;\nusing System;\nclass Keep { void M() { using var resource = Open(); await using var other = Open(); using (var scope = Open()) { Work(); } } }\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", null, Hint);

        Assert.True(result.Changed);
        Assert.DoesNotContain("System", result.Text, StringComparison.Ordinal);
        Assert.Contains("class Keep {", result.Text, StringComparison.Ordinal);
        Assert.Contains("using var resource = Open();", result.Text, StringComparison.Ordinal);
        Assert.Contains("await using var other = Open();", result.Text, StringComparison.Ordinal);
        Assert.Contains("using (var scope = Open()) { Work(); }", result.Text, StringComparison.Ordinal);
    }
    [Fact]
    public void Globally_and_alias_qualified_imports_are_removed()
    {
        const string source = "global using global::System.Text;\nusing @record = global::System.Text.StringBuilder;\nusing Path = External::System.IO.Path;\nclass Keep { }\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal($"class Keep {{ }}\n{Hint}", result.Text);
    }

    [Fact]
    public void Renaming_is_lexical_deterministic_and_avoids_existing_short_names()
    {
        const string source = "// a very long removable comment provides enough savings for the complete hint and map too\nint a = 1;\nint VeryLongVariable = 2;\nVeryLongVariable += VeryLongVariable;\nPrint(VeryLongVariable, \"VeryLongVariable\");\n";
        var options = new CodeReductionOptions(RenameIdentifiers: true);
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", options, Hint);
        var again = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", options, Hint);

        Assert.True(result.Changed);
        Assert.Equal("VeryLongVariable", result.RenameMap["b"]);
        Assert.Single(result.RenameMap);
        Assert.Contains("int b = 2;", result.Text, StringComparison.Ordinal);
        Assert.Contains("b += b;", result.Text, StringComparison.Ordinal);
        Assert.Contains("Print(b, \"VeryLongVariable\");", result.Text, StringComparison.Ordinal);
        Assert.Contains($"b:VeryLongVariable\n{Hint}", result.Text, StringComparison.Ordinal);
        Assert.Equal(result.Text, again.Text);
    }
    [Theory]
    [InlineData("1UL", "2UL")]
    [InlineData("1e10", "2e10")]
    [InlineData("0xFF", "0xFF")]
    public void Numeric_suffixes_are_not_renamed_as_identifiers(string first, string second)
    {
        var source = $"// a very long removable comment provides enough savings for the complete hint and map too\nvar first = {first};\nvar second = {second};\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", new CodeReductionOptions(RenameIdentifiers: true), Hint);

        Assert.True(result.Changed);
        Assert.Empty(result.RenameMap);
        Assert.Contains(first, result.Text, StringComparison.Ordinal);
        Assert.Contains(second, result.Text, StringComparison.Ordinal);
    }
    [Fact]
    public void Options_independently_preserve_comments_imports_and_original_names()
    {
        var source = $"using System.Text;\n// LongVariable stays readable in this comment\nint LongVariable = 1;\nLongVariable++;\n{new string('\n', 100)}";
        var reducer = new CodeReducer(new CSharpCodeLexer());
        var keepComments = reducer.Reduce(source, "Example.cs", new CodeReductionOptions(RemoveComments: false), Hint);
        var keepImports = reducer.Reduce(source, "Example.cs", new CodeReductionOptions(RemoveImports: false), Hint);
        var keepAll = reducer.Reduce(source, "Example.cs", new CodeReductionOptions(false, false, false), Hint);
        var renameWithComments = reducer.Reduce(source, "Example.cs", new CodeReductionOptions(false, true, true), Hint);

        Assert.True(keepComments.Changed);
        Assert.Contains("// LongVariable stays readable in this comment", keepComments.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("using System.Text;", keepComments.Text, StringComparison.Ordinal);
        Assert.True(keepImports.Changed);
        Assert.Contains("using System.Text;", keepImports.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("//", keepImports.Text, StringComparison.Ordinal);
        Assert.Contains("using System.Text;", keepAll.Text, StringComparison.Ordinal);
        Assert.Contains("// LongVariable", keepAll.Text, StringComparison.Ordinal);
        Assert.Contains("int LongVariable", keepAll.Text, StringComparison.Ordinal);
        Assert.Empty(keepAll.RenameMap);
        Assert.Contains("// LongVariable stays readable in this comment", renameWithComments.Text, StringComparison.Ordinal);
        Assert.Contains("int a = 1;", renameWithComments.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_overhead_can_remove_all_apparent_rename_savings()
    {
        const string source = "int Abcd; Abcd++;\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", new CodeReductionOptions(false, false, true), "h");

        Assert.False(result.Changed);
        Assert.Equal(source, result.Text);
        Assert.Empty(result.RenameMap);
    }

    [Fact]
    public void A_single_unnumbered_line_prevents_stripping_any_prefix()
    {
        const string source = "  60\t// a very long removable comment provides enough savings for the complete hint too\n\n  62\t    int value = 1;\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", null, Hint);

        Assert.False(result.Changed);
        Assert.Equal(source, result.Text);
    }

    [Fact]
    public void Missing_hint_returns_original_even_when_all_lines_are_numbered()
    {
        const string source = "1\t// a very long removable comment provides enough savings for a hint\n2\tint value;\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", null, null);

        Assert.False(result.Changed);
        Assert.Equal(source, result.Text);
    }

    [Theory]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void CSharp_newlines_after_comments_do_not_swallow_following_code(string newline)
    {
        var source = $"// a long comment that provides more savings than the hint but must not swallow the following code{newline}int Important = 42;\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", null, Hint);

        Assert.True(result.Supported);
        Assert.Contains("int Important = 42;", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"first\"u8", "\"second\"u8")]
    [InlineData("\"\"\"first\"\"\"u8", "\"\"\"second\"\"\"u8")]
    public void UTF8_string_suffixes_remain_part_of_the_literal(string first, string second)
    {
        var source = $"// a very long removable comment provides enough savings for the complete hint and map too\nvar first = {first};\nvar second = {second};\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", new CodeReductionOptions(RenameIdentifiers: true), Hint);

        Assert.True(result.Changed);
        Assert.Empty(result.RenameMap);
        Assert.Contains(first, result.Text, StringComparison.Ordinal);
        Assert.Contains(second, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_short_names_skip_CSharp_keywords()
    {
        var source = string.Concat(Enumerable.Range(0, 45).Select(index =>
            $"int LongIdentifier{index} = 0;\nLongIdentifier{index}++;\n"));
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", new CodeReductionOptions(RenameIdentifiers: true), Hint);

        Assert.True(result.Changed);
        Assert.Equal(45, result.RenameMap.Count);
        Assert.DoesNotContain("as", result.RenameMap.Keys);
        Assert.Equal("LongIdentifier44", result.RenameMap["at"]);
        Assert.Contains("int at = 0;\nat++;", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Escaped_identifier_and_plain_occurrences_rename_as_one_name()
    {
        const string source = "// a very long comment leaves room for the rename map and complete hint\nint @café = 1;\n@café += café;\n";
        var result = new CodeReducer(new CSharpCodeLexer()).Reduce(source, "Example.cs", new CodeReductionOptions(RenameIdentifiers: true), Hint);

        Assert.True(result.Changed);
        Assert.Equal("café", result.RenameMap["a"]);
        Assert.Contains("int a = 1;\na += a;", result.Text, StringComparison.Ordinal);
    }
}
