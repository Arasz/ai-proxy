using AiProxy.Core.Content;

namespace AiProxy.Tests.Unit.Content;

/// <summary>
/// The decomposer with a file path, stated as behaviour: a source file read whole is one
/// source-code part, and an unknown or missing path changes nothing.
/// </summary>
public sealed class ContentDecompositorFilePathTests
{
    private const string Text = """
        This file explains the build.

        It has two paragraphs of plain words.
        """;

    private static readonly ContentDecompositor Decompositor = new();

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("src/PROGRAM.CS")]
    [InlineData("tools/run.py")]
    [InlineData("web/app.js")]
    [InlineData("web/app.mjs")]
    [InlineData("web/app.cjs")]
    [InlineData("web/app.ts")]
    [InlineData("web/App.tsx")]
    public void A_file_with_a_source_code_extension_is_one_source_code_part(string path)
    {
        var decomposition = Decompositor.Decompose(Text, "request", path);

        var part = Assert.Single(decomposition.Parts);
        Assert.Equal(ContentType.SourceCode, part.Type);
        Assert.Equal(Text, part.Text);
        Assert.Equal(path, part.FilePath);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("src/Program.cs.bak")]
    [InlineData("Makefile")]
    [InlineData("src/.cs")]
    [InlineData(null)]
    public void A_file_with_an_unknown_or_no_extension_does_not_route_as_source_code(string? path)
    {
        var decomposition = Decompositor.Decompose(Text, "request", path);

        Assert.DoesNotContain(decomposition.Parts, part => part.Type == ContentType.SourceCode);
        Assert.Equal(
            Decompositor.Decompose(Text, "request").Parts.Select(part => (part.Type, part.Text)),
            decomposition.Parts.Select(part => (part.Type, part.Text)));
    }

    [Fact]
    public void The_parts_of_a_file_with_an_unknown_extension_carry_its_path()
    {
        var decomposition = Decompositor.Decompose(Text, "request", "notes.txt");

        Assert.All(decomposition.Parts, part => Assert.Equal("notes.txt", part.FilePath));
    }

    [Fact]
    public void A_source_code_file_keeps_its_place_in_the_template()
    {
        var decomposition = Decompositor.Decompose(Text, "request", "src/Program.cs");

        Assert.Equal(ContentDecompositor.Placeholder(ContentType.SourceCode, 1), decomposition.Template);
    }
}
