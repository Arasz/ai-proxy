using System.Text;
using System.Text.Json;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Providers;
using AiProxy.Tests.Unit.Content.Ccr;

namespace AiProxy.Tests.Unit.Content;

/// <summary>The size minimum is judged on a whole region, and then every fragment of it is reduced.</summary>
public sealed class RegionMinimumTests
{
    private const string BelowMinimum = "below-minimum";

    private static string Of(char letter, int length)
    {
        return new string(letter, length);
    }

    private static async Task<(CompressionResult Result, List<CompressedPart> Parts, List<int> Reduced, byte[] Body)> RunAsync(
        params (ContentType Type, string Text)[] fragments)
    {
        var decomposer = new FragmentDecompositor(fragments);
        var executor = new ShrinkingExecutor();
        var parts = new List<CompressedPart>();
        var store = new CapturingStore();
        var pipeline = new ContentCompressionPipeline(
            new ToolOutputRegionFinder(),
            decomposer,
            executor,
            new ContentCompositor(),
            store,
            CompressionOptions.Default,
            TimeProvider.System,
            new CcrPreviewer(new MemoryOriginalStore(), () => new CcrPreviewBuilder(new CcrPreviewOverlay()), CcrOptions.Default));
        var body = Encoding.UTF8.GetBytes($$"""{"messages":[{"role":"tool","content":{{JsonSerializer.Serialize(string.Concat(fragments.Select(f => f.Text)))}}}]}""");

        var result = await pipeline.CompressAsync(new CompressionRequest(body, "application/json", Dialect.OpenAi), CancellationToken.None);
        parts.AddRange(store.Records.SelectMany(record => record.Parts));
        return (result, parts, executor.Reduced, body);
    }

    [Fact]
    public async Task Short_fragments_of_a_large_region_all_reach_their_reducer()
    {
        var (result, parts, reduced, _) = await RunAsync(
            (ContentType.Markdown, Of('a', 60)),
            (ContentType.Prose, Of('b', 60)),
            (ContentType.Markdown, Of('c', 60)),
            (ContentType.Prose, Of('d', 60)),
            (ContentType.Markdown, Of('e', 60)),
            (ContentType.Prose, Of('f', 60)));

        Assert.Equal([1, 2, 3, 4, 5, 6], reduced);
        Assert.DoesNotContain(parts, part => part.Path == BelowMinimum);
        Assert.True(result.Changed);
    }

    [Fact]
    public async Task A_region_under_the_minimum_of_its_dominant_kind_is_left_whole()
    {
        var (result, parts, reduced, body) = await RunAsync(
            (ContentType.Markdown, Of('a', 50)),
            (ContentType.Prose, Of('b', 60)),
            (ContentType.Markdown, Of('c', 40)));

        Assert.Empty(reduced);
        Assert.False(result.Changed);
        Assert.Equal(body, result.Body.ToArray());
        Assert.All(parts, part => Assert.Equal(BelowMinimum, part.Path));
    }

    [Fact]
    public async Task The_minimum_is_the_one_of_the_kind_holding_most_characters()
    {
        var (_, parts, reduced, _) = await RunAsync(
            (ContentType.Prose, Of('a', 40)),
            (ContentType.Markdown, Of('b', 210)));

        Assert.Equal([1, 2], reduced);
        Assert.DoesNotContain(parts, part => part.Path == BelowMinimum);
    }

    [Fact]
    public async Task A_switched_off_kind_stays_unreduced_inside_a_large_region()
    {
        var (_, parts, reduced, _) = await RunAsync(
            (ContentType.Markdown, Of('a', 600)),
            (ContentType.DirectoryListing, Of('b', 100)),
            (ContentType.Markdown, Of('c', 100)));

        Assert.Equal([1, 3], reduced);
        Assert.Equal(BelowMinimum, parts.Single(part => part.Type == ContentType.DirectoryListing).Path);
    }

    private sealed class FragmentDecompositor((ContentType Type, string Text)[] fragments) : IDecompositor
    {
        public Decomposition Decompose(string content, string requestId, string? filePath = null)
        {
            var parts = fragments.Select((fragment, index) => new ContentPart(requestId, index + 1, fragment.Type, fragment.Text)).ToList();
            var template = string.Concat(parts.Select(part => part.Type.Placeholder(part.ChunkId)));
            return new Decomposition(requestId, content, template, parts);
        }
    }

    private sealed class ShrinkingExecutor : ICompressionExecutor
    {
        public List<int> Reduced { get; } = [];

        public ValueTask<CompressedPart> ExecuteAsync(ContentPart part, CancellationToken cancellationToken)
        {
            Reduced.Add(part.ChunkId);
            var text = part.Text[..^1];
            return ValueTask.FromResult(new CompressedPart(part.ChunkId, part.Type, text, part.Text, "test.shrink", (double)text.Length / part.Text.Length));
        }
    }

    private sealed class CapturingStore : IContentStore
    {
        public List<ContentRecord> Records { get; } = [];

        public ValueTask StoreAsync(ContentRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ContentRecord?> GetAsync(string requestId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(Records.FirstOrDefault(record => record.RequestId == requestId));
        }
    }
}
