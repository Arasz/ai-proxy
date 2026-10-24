using AiProxy.Core.Content.Code;

namespace AiProxy.Tests.Unit.Content.Code;

public sealed class CodeReducerTests
{
    private const string Hint = "[fixture: fetch original before editing]";

    [Fact]
    public void Comments_and_blank_lines_are_removed_but_indentation_survives()
    {
        const string source = "// A deliberately long comment makes the complete compacted view smaller than the original.\n\n    int value = 1; // another long removable comment\n";

        var result = new CodeReducer().Reduce(source, "Example.cs", new CodeReductionOptions(), Hint);

        Assert.True(result.Changed);
        Assert.True(result.Supported);
        Assert.Equal("    int value = 1;\n" + Hint, result.Text);
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
        var result = new CodeReducer().Reduce(source, path, null, hint);

        Assert.False(result.Changed);
        Assert.Equal(source, result.Text);
    }

    [Fact]
    public void Multiline_literals_keep_blank_lines_and_trailing_spaces()
    {
        const string source = "// a very long removable comment provides enough savings for the complete hint too\nvar text = \"\"\"\n    first   \n\n    last  \n    \"\"\";\n";
        var result = new CodeReducer().Reduce(source, "Example.cs", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal("var text = \"\"\"\n    first   \n\n    last  \n    \"\"\";\n" + Hint, result.Text);
    }

    [Fact]
    public void Inline_comment_removal_keeps_identifier_separation()
    {
        const string source = "// a very long removable comment provides enough savings for the complete hint too\nint/*removed*/value = 1;\n";
        var result = new CodeReducer().Reduce(source, "Example.cs", null, Hint);

        Assert.True(result.Changed);
        Assert.Contains("int           value", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_numbered_CRLF_input_is_byte_exact()
    {
        const string source = "  1\t// a long comment that must remain untouched\r\n  2\t#if DEBUG\r\n  3\t    int value = 1;\r\n  4\t#endif\r\n";
        var result = new CodeReducer().Reduce(source, "Example.cs", null, Hint);

        Assert.False(result.Changed);
        Assert.False(result.Supported);
        Assert.Equal(source, result.Text);
    }

    [Fact]
    public void Equal_size_including_hint_returns_original()
    {
        const string source = "// removed\nint x;\n";
        var hint = new string('h', "// removed\n".Length);
        var result = new CodeReducer().Reduce(source, "Example.cs", null, hint);

        Assert.False(result.Changed);
        Assert.Equal(source, result.Text);
    }
    [Fact]
    public void Every_number_prefix_survives_removable_gaps_and_CRLF()
    {
        const string source = "  60\t// a very long removable comment provides enough savings for the complete hint too\r\n  61\t\r\n  62\t    int value = 1;\r\n";
        var result = new CodeReducer().Reduce(source, "Example.cs", null, Hint);

        Assert.True(result.Changed);
        Assert.Equal("  62\t    int value = 1;\r\n" + Hint, result.Text);
    }
    [Fact]
    public void Only_complete_using_directives_are_removed()
    {
        const string source = "global using System.Text;\nusing static System.Math;\nusing Alias = System.Collections.Generic;\nusing\n    System.Threading;\nusing System; class Keep {}\nusing var resource = Open();\nawait using var other = Open();\nusing (var scope = Open()) { Work(); }\n";
        var result = new CodeReducer().Reduce(source, "Example.cs", null, Hint);

        Assert.True(result.Changed);
        Assert.DoesNotContain("System", result.Text, StringComparison.Ordinal);
        Assert.Contains(" class Keep {}", result.Text, StringComparison.Ordinal);
        Assert.Contains("using var resource = Open();", result.Text, StringComparison.Ordinal);
        Assert.Contains("await using var other = Open();", result.Text, StringComparison.Ordinal);
        Assert.Contains("using (var scope = Open()) { Work(); }", result.Text, StringComparison.Ordinal);
    }
    [Fact]
    public void Renaming_is_lexical_deterministic_and_avoids_existing_short_names()
    {
        const string source = "// a very long removable comment provides enough savings for the complete hint and map too\nint a = 1;\nint VeryLongVariable = 2;\nVeryLongVariable += VeryLongVariable;\nPrint(VeryLongVariable, \"VeryLongVariable\");\n";
        var options = new CodeReductionOptions(RenameIdentifiers: true);
        var result = new CodeReducer().Reduce(source, "Example.cs", options, Hint);
        var again = new CodeReducer().Reduce(source, "Example.cs", options, Hint);

        Assert.True(result.Changed);
        Assert.Equal("VeryLongVariable", result.RenameMap["b"]);
        Assert.Single(result.RenameMap);
        Assert.Contains("int b = 2;", result.Text, StringComparison.Ordinal);
        Assert.Contains("b += b;", result.Text, StringComparison.Ordinal);
        Assert.Contains("Print(b, \"VeryLongVariable\");", result.Text, StringComparison.Ordinal);
        Assert.Contains("b:VeryLongVariable\n" + Hint, result.Text, StringComparison.Ordinal);
        Assert.Equal(result.Text, again.Text);
    }
}
