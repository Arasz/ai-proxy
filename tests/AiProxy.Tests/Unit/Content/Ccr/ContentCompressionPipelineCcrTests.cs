using System.Text;
using System.Text.Json;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Content.Handlers;
using AiProxy.Core.Providers;

namespace AiProxy.Tests.Unit.Content.Ccr;

/// <summary>The CCR stage of the pipeline: a large tool result becomes its preview, and its original is kept first when the thread needs it.</summary>
public sealed class ContentCompressionPipelineCcrTests
{
    private const string Small = "one line of output";
    private static readonly string Large = string.Join("\n", Enumerable.Range(1, 600).Select(index => $"step {index:D4}: compiled module m{index} into obj/m{index}.o"));

    private static byte[] Body(params string[] outputs)
    {
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = outputs.Select((output, index) => new { type = "tool_result", tool_use_id = $"t{index}", content = output }).ToArray()
                }
            }
        }));
    }

    private static ContentCompressionPipeline Build(MemoryOriginalStore store)
    {
        return new ContentCompressionPipeline(
            new ToolOutputRegionFinder(),
            new ContentDecompositor(),
            new CompressionExecutor(new ContentRouter([], new PassThroughCompressionHandler())),
            new ContentCompositor(),
            new NullContentStore(),
            CompressionOptions.Default,
            TimeProvider.System,
            new CcrPreviewer(store, () => new CcrPreviewBuilder(new CcrPreviewOverlay()), CcrOptions.Default));
    }

    private static string[] ToolResults(CompressionResult result)
    {
        using var document = JsonDocument.Parse(result.Body);
        return document.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray()
            .Select(block => block.GetProperty("content").GetString()!)
            .ToArray();
    }

    [Fact]
    public async Task A_large_stateless_tool_result_is_sent_as_its_preview_and_its_original_comes_back_with_the_result()
    {
        var store = new MemoryOriginalStore();

        var result = await Build(store).CompressAsync(new CompressionRequest(Body(Large, Small), "application/json", Dialect.Anthropic) { Ccr = CcrMode.Stateless }, CancellationToken.None);

        var sent = ToolResults(result);
        Assert.StartsWith(CcrPreview.Marker, sent[0], StringComparison.Ordinal);
        Assert.True(sent[0].Length < Large.Length / 2);
        Assert.Equal(Small, sent[1]);
        var (id, original) = Assert.Single(result.Originals);
        Assert.Equal(Large, original);
        Assert.Contains(id, sent[0], StringComparison.Ordinal);
        Assert.Empty(store.Originals);
    }

    [Fact]
    public async Task A_thread_keeps_the_original_before_it_sends_the_preview()
    {
        var store = new MemoryOriginalStore();

        var result = await Build(store).CompressAsync(new CompressionRequest(Body(Large), "application/json", Dialect.Anthropic) { Ccr = CcrMode.Thread }, CancellationToken.None);

        Assert.StartsWith(CcrPreview.Marker, ToolResults(result)[0], StringComparison.Ordinal);
        var kept = Assert.Single(store.Originals);
        Assert.Equal(Large, kept.Value);
        Assert.Equal(kept.Key, Assert.Single(result.Originals).Key);
    }

    [Fact]
    public async Task A_thread_whose_store_fails_gets_no_preview_and_the_failure_is_reported()
    {
        var store = new MemoryOriginalStore { Failure = new IOException("disk full") };

        var result = await Build(store).CompressAsync(new CompressionRequest(Body(Large), "application/json", Dialect.Anthropic) { Ccr = CcrMode.Thread }, CancellationToken.None);

        Assert.DoesNotContain(CcrPreview.Marker, Encoding.UTF8.GetString(result.Body.Span), StringComparison.Ordinal);
        Assert.Empty(result.Originals);
        var failure = Assert.Single(result.PartFailures);
        Assert.IsType<IOException>(failure.Exception);
    }

    [Fact]
    public async Task With_ccr_off_a_large_result_is_never_previewed()
    {
        var store = new MemoryOriginalStore();

        var result = await Build(store).CompressAsync(new CompressionRequest(Body(Large), "application/json", Dialect.Anthropic), CancellationToken.None);

        Assert.DoesNotContain(CcrPreview.Marker, Encoding.UTF8.GetString(result.Body.Span), StringComparison.Ordinal);
        Assert.Empty(result.Originals);
    }

    [Fact]
    public async Task The_same_original_gives_the_same_preview_bytes_on_every_send()
    {
        var first = await Build(new MemoryOriginalStore()).CompressAsync(new CompressionRequest(Body(Large), null, Dialect.Anthropic) { Ccr = CcrMode.Stateless }, CancellationToken.None);
        var second = await Build(new MemoryOriginalStore()).CompressAsync(new CompressionRequest(Body(Large), null, Dialect.Anthropic) { Ccr = CcrMode.Stateless }, CancellationToken.None);

        Assert.Equal(first.Body.ToArray(), second.Body.ToArray());
    }

    private sealed class NullContentStore : IContentStore
    {
        public ValueTask StoreAsync(ContentRecord record, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<ContentRecord?> GetAsync(string requestId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ContentRecord?>(null);
        }
    }
}