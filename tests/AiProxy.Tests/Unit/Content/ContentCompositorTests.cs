using AiProxy.Core.Content;

namespace AiProxy.Tests.Unit.Content;

/// <summary>The compositor, stated as behaviour: the parts go back where they came from.</summary>
public sealed class ContentCompositorTests
{
    private const string Mixed = """
        ProviderDetector.cs
        UpstreamForwarder.cs

        src/foo.cs:12: matched line
        src/bar.cs:44: another line

        { "a": 1, "b": [1, 2] }
        """;

    private static readonly ContentDecompositor Decompositor = new();
    private static readonly ContentCompositor Compositor = new();

    [Fact]
    public void The_original_comes_back_when_nothing_was_reduced()
    {
        var decomposition = Decompositor.Decompose(Mixed, "request");
        var parts = decomposition.Parts
            .Select(part => new CompressedPart(part.ChunkId, part.Type, part.Text, part.Text, "test", 1.0))
            .ToList();

        var composed = Compositor.Compose(decomposition, parts);

        Assert.Equal(Mixed, composed);
    }

    [Fact]
    public void A_reduced_part_takes_the_place_of_its_own()
    {
        var decomposition = Decompositor.Decompose(Mixed, "request");
        var parts = decomposition.Parts
            .Select(part => new CompressedPart(part.ChunkId, part.Type, part.Text, part.Text, "test", 1.0))
            .ToList();
        parts[1] = parts[1] with { Text = "src/foo.cs:12: matched line" };

        var composed = Compositor.Compose(decomposition, parts);

        Assert.Contains("src/foo.cs:12: matched line", composed, StringComparison.Ordinal);
        Assert.DoesNotContain("src/bar.cs:44: another line", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_part_that_was_not_reduced_comes_back_as_it_was()
    {
        var decomposition = Decompositor.Decompose(Mixed, "request");
        var parts = new List<CompressedPart>
        {
            new(1, ContentType.DirectoryListing, "only the first part", "the first part", "test", 0.5),
        };

        var composed = Compositor.Compose(decomposition, parts);

        Assert.Contains("src/bar.cs:44: another line", composed, StringComparison.Ordinal);
    }
}
