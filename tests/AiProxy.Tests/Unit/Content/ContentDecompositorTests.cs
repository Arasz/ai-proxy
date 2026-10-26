using AiProxy.Core.Content;

namespace AiProxy.Tests.Unit.Content;

/// <summary>
///     The decomposer, stated as behaviour: a mixed payload is split by structure, and the
///     template keeps everything that is not a part.
/// </summary>
public sealed class ContentDecompositorTests
{
    private const string Mixed = """
                                 ProviderDetector.cs
                                 UpstreamForwarder.cs

                                 src/foo.cs:12: matched line
                                 src/bar.cs:44: another line

                                 { "a": 1, "b": [1, 2] }
                                 """;

    private static readonly ContentDecompositor Decompositor = new();

    [Fact]
    public void A_listing_a_grep_result_and_a_json_blob_are_three_parts()
    {
        var decomposition = Decompositor.Decompose(Mixed, "request");

        Assert.Equal(3, decomposition.Parts.Count);
        Assert.Equal(ContentType.DirectoryListing, decomposition.Parts[0].Type);
        Assert.Equal(ContentType.SourceCode, decomposition.Parts[1].Type);
        Assert.Equal(ContentType.Json, decomposition.Parts[2].Type);
    }

    [Fact]
    public void The_parts_are_numbered_from_one_in_the_order_they_appeared()
    {
        var decomposition = Decompositor.Decompose(Mixed, "request");

        Assert.Equal([1, 2, 3], decomposition.Parts.Select(part => part.ChunkId));
    }

    [Fact]
    public void The_parts_carry_the_text_they_were_cut_from()
    {
        var decomposition = Decompositor.Decompose(Mixed, "request");

        Assert.Equal("ProviderDetector.cs\nUpstreamForwarder.cs", decomposition.Parts[0].Text);
    }

    [Fact]
    public void The_parts_carry_the_request_they_belong_to()
    {
        var decomposition = Decompositor.Decompose(Mixed, "request-7");

        Assert.All(decomposition.Parts, part => Assert.Equal("request-7", part.RequestId));
    }

    [Fact]
    public void The_template_keeps_the_text_between_the_parts()
    {
        var decomposition = Decompositor.Decompose(Mixed, "request");

        Assert.Contains("{listing_1}", decomposition.Template, StringComparison.Ordinal);
        Assert.Contains("{code_2}", decomposition.Template, StringComparison.Ordinal);
        Assert.Contains("{json_3}", decomposition.Template, StringComparison.Ordinal);
    }

    [Fact]
    public void The_template_holds_every_character_that_is_not_a_part()
    {
        var decomposition = Decompositor.Decompose(Mixed, "request");

        var restored = decomposition.Template
            .Replace("{listing_1}", decomposition.Parts[0].Text, StringComparison.Ordinal)
            .Replace("{code_2}", decomposition.Parts[1].Text, StringComparison.Ordinal)
            .Replace("{json_3}", decomposition.Parts[2].Text, StringComparison.Ordinal);

        Assert.Equal(Mixed, restored);
    }

    [Fact]
    public void A_payload_with_no_structure_stays_one_part()
    {
        var decomposition = Decompositor.Decompose("just a sentence", "request");

        Assert.Single(decomposition.Parts);
        Assert.Equal(ContentType.Prose, decomposition.Parts[0].Type);
    }

    [Fact]
    public void A_fenced_code_block_is_one_part()
    {
        var payload = "before\n\n```csharp\nvar a = 1;\n\nvar b = 2;\n```\n\nafter";

        var decomposition = Decompositor.Decompose(payload, "request");

        Assert.Equal(3, decomposition.Parts.Count);
        Assert.Equal(ContentType.SourceCode, decomposition.Parts[1].Type);
        Assert.Contains("var a = 1;", decomposition.Parts[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_that_looks_like_json_but_does_not_parse_stays_text()
    {
        var payload = "{ this is not json at all";

        var decomposition = Decompositor.Decompose(payload, "request");

        Assert.Single(decomposition.Parts);
        Assert.Equal(ContentType.Prose, decomposition.Parts[0].Type);
    }

    [Fact]
    public void Two_blocks_of_the_same_kind_with_only_whitespace_between_them_are_one_part()
    {
        var payload = "src/a.cs:1: one\nsrc/b.cs:2: two\n\nsrc/c.cs:3: three\nsrc/d.cs:4: four";

        var decomposition = Decompositor.Decompose(payload, "request");

        Assert.Single(decomposition.Parts);
        Assert.Equal(ContentType.SourceCode, decomposition.Parts[0].Type);
    }

    [Fact]
    public void A_shell_transcript_is_a_part_of_its_own()
    {
        var payload = "$ ls -la\ntotal 4\n$ echo done";

        var decomposition = Decompositor.Decompose(payload, "request");

        Assert.Equal(ContentType.ShellTranscript, Assert.Single(decomposition.Parts).Type);
    }

    [Fact]
    public void A_configuration_file_is_a_part_of_its_own()
    {
        var payload = "key = \"value\"\nother = 2\nname = \"thing\"";

        var decomposition = Decompositor.Decompose(payload, "request");

        Assert.Equal(ContentType.Toml, Assert.Single(decomposition.Parts).Type);
    }

    [Fact]
    public void A_markdown_document_is_a_part_of_its_own()
    {
        // A blank line ends a block, so the heading and the paragraph under it are two
        // parts, and the heading is the one that says the document is Markdown.
        var payload = "# Title\n\nSome text under the title.";

        var decomposition = Decompositor.Decompose(payload, "request");

        Assert.Equal(ContentType.Markdown, decomposition.Parts[0].Type);
    }

    [Fact(Skip = TimingDependent.Reason)]
    public void A_payload_of_lines_that_open_a_value_and_close_none_is_decomposed_quickly()
    {
        // Looking for a JSON value that never balances costs a scan to the end of the
        // text, so a payload of such lines costs a scan per line. The decomposer gives
        // up looking after a few failures, and a body of a megabyte must not take
        // minutes of one request's CPU.
        var payload = string.Join("\n", Enumerable.Range(0, 48_000).Select(index => $"{{\"unclosed\": {index}"));

        var started = DateTimeOffset.UtcNow;
        var decomposition = Decompositor.Decompose(payload, "request");
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Single(decomposition.Parts);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(5),
            $"the decomposition of {payload.Length} bytes took {elapsed.TotalSeconds:F1} seconds");
    }

    [Fact]
    public void The_decomposition_says_how_many_parts_it_made()
    {
        var decomposition = Decompositor.Decompose(Mixed, "request");

        Assert.Equal(3, decomposition.ChunkCount);
    }
}