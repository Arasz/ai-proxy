using AiProxy.Code;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Content.Code;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>A source file with a trusted path gets the code view and its retrieval hint; anything else keeps the blank-line path.</summary>
public sealed class SourceCodeCompressionHandlerTests
{
    private const string Source =
        "using System;\nusing System.Linq;\n\n// A long comment the code view drops, so the view is smaller than the file.\npublic sealed class Example\n{\n    public int Value => 1; // another comment the view drops\n}\n";

    private static readonly SourceCodeCompressionHandler Handler = new([new CSharpCodeLexer()]);

    [Fact]
    public async Task A_csharp_file_with_its_path_becomes_a_view_that_names_its_original()
    {
        var result = await Handler.HandleAsync(Part(Source, "/repo/src/Example.cs"), CancellationToken.None);

        Assert.Equal(SourceCodeCompressionHandler.CodeViewPath, result.Path);
        Assert.Equal(CcrView.IdOf(Source), result.RetrievalId);
        Assert.EndsWith(CcrView.Hint(CcrView.IdOf(Source)), result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("A long comment", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("using System", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/repo/src/example.py")]
    public async Task Without_a_path_a_lexer_reads_the_file_keeps_the_blank_line_path(string? path)
    {
        var result = await Handler.HandleAsync(Part(Source, path), CancellationToken.None);

        Assert.Equal(SourceCodeCompressionHandler.HandlerPath, result.Path);
        Assert.Contains("A long comment", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(CcrView.Marker, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_the_view_does_not_shrink_keeps_the_blank_line_path()
    {
        const string bare = "class A\n{\n\n}\n";

        var result = await Handler.HandleAsync(Part(bare, "/repo/A.cs"), CancellationToken.None);

        Assert.Equal(SourceCodeCompressionHandler.HandlerPath, result.Path);
        Assert.Equal("class A\n{\n}", result.Text);
    }

    [Fact]
    public async Task With_no_lexers_a_csharp_file_keeps_the_blank_line_path()
    {
        var result = await new SourceCodeCompressionHandler([]).HandleAsync(Part(Source, "/repo/Example.cs"), CancellationToken.None);

        Assert.Equal(SourceCodeCompressionHandler.HandlerPath, result.Path);
    }

    [Fact]
    public async Task A_python_file_with_its_path_becomes_a_view_that_keeps_its_docstring()
    {
        const string python =
            "import os\nfrom typing import Any\n\n# A long comment the view drops, so the view is smaller than the file.\ndef value(item: Any) -> str:\n    \"\"\"Returns the item's text.\"\"\"\n    return os.fspath(item)  # another comment the view drops\n";

        var result = await new SourceCodeCompressionHandler([new CSharpCodeLexer(), new PythonCodeLexer()]).HandleAsync(Part(python, "/repo/value.py"), CancellationToken.None);

        Assert.Equal(SourceCodeCompressionHandler.CodeViewPath, result.Path);
        Assert.Equal(CcrView.IdOf(python), result.RetrievalId);
        Assert.EndsWith(CcrView.Hint(CcrView.IdOf(python)), result.Text, StringComparison.Ordinal);
        Assert.Contains("\"\"\"Returns the item's text.\"\"\"", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("long comment", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("from typing", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_ccr_off_a_csharp_file_keeps_the_blank_line_path()
    {
        var result = await Handler.HandleAsync(Part(Source, "/repo/Example.cs") with { Ccr = CcrMode.Off }, CancellationToken.None);

        Assert.Equal(SourceCodeCompressionHandler.HandlerPath, result.Path);
        Assert.DoesNotContain(CcrView.Marker, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_numbered_read_keeps_its_line_numbers_and_names_the_numbered_original()
    {
        var numbered = string.Concat(Source.TrimEnd('\n').Split('\n').Select((line, index) => $"{index + 1,6}\t{line}\n"));

        var result = await Handler.HandleAsync(Part(numbered, "/repo/Example.cs"), CancellationToken.None);

        Assert.Equal(SourceCodeCompressionHandler.CodeViewPath, result.Path);
        Assert.EndsWith(CcrView.Hint(CcrView.IdOf(numbered)), result.Text, StringComparison.Ordinal);
        Assert.Contains("     5\tpublic sealed class Example", result.Text, StringComparison.Ordinal);
    }

    private static ContentPart Part(string text, string? path) =>
        new("request", 1, ContentType.SourceCode, text, path, SourceCodeFiles.LanguageOf(path), CcrMode.Stateless);
}
