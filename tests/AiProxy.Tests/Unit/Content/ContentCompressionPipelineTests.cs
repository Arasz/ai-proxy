using System.Text;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;
using AiProxy.Core.Providers;
using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests.Unit.Content;

/// <summary>
/// The pipeline, stated as behaviour: the tool output is smaller, the rest of the body is
/// exactly what the client sent, and the record says what happened.
/// </summary>
public sealed class ContentCompressionPipelineTests
{
    private const string ToolOutput = """
        2026-03-12 10:00:00 INFO worker=api-1 request started
        2026-03-12 10:00:01 INFO worker=api-1 request failed status=503 dur_ms=3110
        2026-03-12 10:00:02 INFO worker=api-1 nothing to see
        2026-03-12 10:00:03 INFO worker=api-1 nothing to see
        2026-03-12 10:00:04 INFO worker=api-1 nothing to see
        2026-03-12 10:00:05 INFO worker=api-1 nothing to see
        """;

    private static string Body(string toolOutput) =>
        $$"""{"messages":[{"role":"tool","content":{{System.Text.Json.JsonSerializer.Serialize(toolOutput)}}}]}""";

    private static (ContentCompressionPipeline Pipeline, RecordingContentStore Store) Build(
        IContentStore? store = null,
        CompressionOptions? options = null,
        TimeProvider? time = null,
        ICompressionHandler? logHandler = null)
    {
        var recording = store as RecordingContentStore ?? new RecordingContentStore();
        var router = new ContentRouter(
            [
                logHandler ?? new LogCompressionHandler(),
                new JsonCompressionHandler(),
                new ProseCompressionHandler(),
            ],
            new PassThroughCompressionHandler());

        var pipeline = new ContentCompressionPipeline(
            new ToolOutputRegionFinder(),
            new ContentDecompositor(),
            new CompressionExecutor(router),
            new ContentCompositor(),
            recording,
            options ?? CompressionOptions.Default,
            time ?? TimeProvider.System);

        return (pipeline, recording);
    }

    [Fact]
    public async Task The_tool_output_in_the_body_becomes_smaller()
    {
        var (pipeline, _) = Build();
        var body = Encoding.UTF8.GetBytes(Body(ToolOutput));

        var result = await pipeline.CompressAsync(
            new CompressionRequest(body, "application/json", Dialect.OpenAi),
            CancellationToken.None);

        Assert.True(result.Changed);
        Assert.True(result.Body.Length < body.Length);
    }

    [Fact]
    public async Task The_rest_of_the_body_is_exactly_what_the_client_sent()
    {
        var (pipeline, _) = Build();
        var body = Encoding.UTF8.GetBytes(Body(ToolOutput));

        var result = await pipeline.CompressAsync(
            new CompressionRequest(body, "application/json", Dialect.OpenAi),
            CancellationToken.None);

        var before = Encoding.UTF8.GetString(body);
        var after = Encoding.UTF8.GetString(result.Body.ToArray());
        Assert.StartsWith("""{"messages":[{"role":"tool","content":""", after, StringComparison.Ordinal);
        Assert.EndsWith("""}]}""", after, StringComparison.Ordinal);
        Assert.Equal(before.Split("\"content\":")[0], after.Split("\"content\":")[0]);
    }

    [Fact]
    public async Task A_body_without_tool_output_is_forwarded_unchanged()
    {
        var (pipeline, store) = Build();
        var body = Encoding.UTF8.GetBytes("""{"messages":[{"role":"user","content":"hello"}]}""");

        var result = await pipeline.CompressAsync(
            new CompressionRequest(body, "application/json", Dialect.OpenAi),
            CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(body.ToArray(), result.Body.ToArray());
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task The_record_carries_the_original_the_compressed_the_ratio_and_the_path()
    {
        var (pipeline, store) = Build();
        var body = Encoding.UTF8.GetBytes(Body(ToolOutput));

        await pipeline.CompressAsync(
            new CompressionRequest(body, "application/json", Dialect.OpenAi),
            CancellationToken.None);

        var record = Assert.Single(store.Records);
        Assert.Equal(Encoding.UTF8.GetString(body), record.Original);
        Assert.True(record.Ratio < 1.0);
        Assert.Contains("log.signal-filter", record.Path, StringComparison.Ordinal);
        Assert.Equal(record.Parts.Count, record.ChunkCount);

        // The ratio is checked against the record's own texts and not against the
        // number the pipeline computed, so a ratio that is wrong in both places at once
        // is still caught.
        Assert.Equal(
            (double)Encoding.UTF8.GetByteCount(record.Compressed) /
            Encoding.UTF8.GetByteCount(record.Original),
            record.Ratio,
            6);
    }

    [Fact]
    public async Task A_store_that_fails_does_not_stop_the_request()
    {
        var store = new RecordingContentStore { Failure = new InvalidOperationException("the disk is full") };
        var (pipeline, _) = Build(store);
        var body = Encoding.UTF8.GetBytes(Body(ToolOutput));

        var result = await pipeline.CompressAsync(
            new CompressionRequest(body, "application/json", Dialect.OpenAi),
            CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Contains("the disk is full", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_part_under_the_minimum_is_left_alone()
    {
        var options = new CompressionOptions(MinimumLengths: new Dictionary<ContentType, int> { [ContentType.Log] = 10_000 });
        var (pipeline, _) = Build(options: options);
        var body = Encoding.UTF8.GetBytes(Body(ToolOutput));

        var result = await pipeline.CompressAsync(
            new CompressionRequest(body, "application/json", Dialect.OpenAi),
            CancellationToken.None);

        Assert.False(result.Changed);
    }

    [Fact]
    public void A_region_that_overlaps_the_one_before_it_is_left_alone()
    {
        // The finder returns the string tokens of a body and the tokens do not overlap,
        // so this cannot happen today. A region that did would make the rebuild write a
        // negative number of bytes, and the request would fail instead of going out.
        var regions = new List<ContentRegion>
        {
            new(0, 100, "first"),
            new(50, 100, "overlapping"),
            new(200, 100, "last"),
        };

        var kept = ContentCompressionPipeline.WithoutOverlaps(regions);

        Assert.Equal(["first", "last"], kept.Select(region => region.Text));
    }

    [Fact]
    public void The_regions_the_finder_found_do_not_overlap()
    {
        // The promise the guard rests on, checked against the finder itself.
        var regions = new ToolOutputRegionFinder().Find(System.Text.Encoding.UTF8.GetBytes(
            """{"messages":[{"role":"tool","content":"one"},{"role":"tool","content":"two"}]}"""));

        var kept = ContentCompressionPipeline.WithoutOverlaps(regions);

        Assert.Equal(regions.Count, kept.Count);
    }

    [Fact]
    public async Task The_record_is_stamped_by_the_time_provider()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var (pipeline, store) = Build(time: time);
        var body = Encoding.UTF8.GetBytes(Body(ToolOutput));

        await pipeline.CompressAsync(
            new CompressionRequest(body, "application/json", Dialect.OpenAi),
            CancellationToken.None);

        Assert.Equal(time.GetUtcNow(), Assert.Single(store.Records).CreatedAt);
    }

    [Fact]
    public async Task A_handler_that_throws_does_not_discard_the_reductions_of_the_other_parts()
    {
        var (pipeline, store) = Build(options: CompressionOptions.WithMinimum(1), logHandler: new ThrowingLogHandler());
        var json = "{\n  \"a\": 1,\n  \"b\": [1, 2, 3]\n}";
        var body = Encoding.UTF8.GetBytes(
            $$"""{"messages":[{"role":"tool","content":{{System.Text.Json.JsonSerializer.Serialize(ToolOutput)}}},{"role":"tool","content":{{System.Text.Json.JsonSerializer.Serialize(json)}}}]}""");

        var result = await pipeline.CompressAsync(
            new CompressionRequest(body, "application/json", Dialect.OpenAi),
            CancellationToken.None);

        Assert.True(result.Changed);
        var text = Encoding.UTF8.GetString(result.Body.ToArray());
        Assert.Contains("nothing to see", text, StringComparison.Ordinal);
        Assert.Contains("""\u0022a\u0022:1,\u0022b\u0022:[1,2,3]""", text, StringComparison.Ordinal);
        var failed = Assert.Single(Assert.Single(store.Records).Parts, part => part.Path == ContentCompressionPipeline.HandlerFailedPath);
        Assert.Equal(ContentType.Log, failed.Type);
        Assert.Equal(failed.OriginalText, failed.Text);
        Assert.Equal(1.0, failed.Ratio);
    }

    [Fact]
    public async Task A_handler_that_throws_is_reported_with_its_part_and_its_exception()
    {
        var (pipeline, _) = Build(options: CompressionOptions.WithMinimum(1), logHandler: new ThrowingLogHandler());
        var body = Encoding.UTF8.GetBytes(Body(ToolOutput));

        var result = await pipeline.CompressAsync(
            new CompressionRequest(body, "application/json", Dialect.OpenAi),
            CancellationToken.None);

        Assert.False(result.Changed);
        var failure = Assert.Single(result.PartFailures);
        Assert.Equal(1, failure.ChunkId);
        Assert.Equal(nameof(ContentType.Log), failure.ContentType);
        Assert.Equal("boom", Assert.IsType<InvalidOperationException>(failure.Exception).Message);
    }

    [Fact]
    public async Task A_cancelled_request_still_propagates_out_of_a_handler()
    {
        var (pipeline, _) = Build(options: CompressionOptions.WithMinimum(1), logHandler: new ThrowingLogHandler(cancelled: true));
        var body = Encoding.UTF8.GetBytes(Body(ToolOutput));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.CompressAsync(new CompressionRequest(body, "application/json", Dialect.OpenAi), source.Token));
    }

    [Fact]
    public async Task Each_step_reports_how_long_it_took_and_reduce_is_kept_per_content_type()
    {
        var time = new FakeTimeProvider();
        var store = new RecordingContentStore { OnStore = () => time.Advance(TimeSpan.FromMilliseconds(3)) };
        var (pipeline, _) = Build(store: store, time: time, logHandler: new SlowHandler(new LogCompressionHandler(), time, TimeSpan.FromMilliseconds(7)));
        var body = Encoding.UTF8.GetBytes(Body(ToolOutput));

        var result = await pipeline.CompressAsync(new CompressionRequest(body, "application/json", Dialect.OpenAi), CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(["find", "decompose", "reduce.Log", "compose", "rebuild", "store"], result.Stages.Select(stage => stage.Name));
        var logParts = Assert.Single(store.Records).Parts.Count(part => part.Type == ContentType.Log && part.Path != "below-minimum");
        Assert.True(logParts > 0, "the body must reach the log reducer, or the timing proves nothing");
        Assert.Equal(TimeSpan.FromMilliseconds(7 * logParts), result.Stages.Single(stage => stage.Name == "reduce.Log").Elapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(3), result.Stages.Single(stage => stage.Name == "store").Elapsed);
        Assert.All(result.Stages.Where(stage => stage.Name is not ("reduce.Log" or "store")), stage => Assert.Equal(TimeSpan.Zero, stage.Elapsed));
    }

    [Fact]
    public async Task A_body_without_tool_output_still_reports_the_search()
    {
        var (pipeline, _) = Build();

        var result = await pipeline.CompressAsync(new CompressionRequest("""{"model":"m"}"""u8.ToArray(), "application/json", Dialect.OpenAi), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(["find"], result.Stages.Select(stage => stage.Name));
    }

    /// <summary>Runs another handler and moves the clock on, so a stage has a known duration.</summary>
    private sealed class SlowHandler(ICompressionHandler inner, FakeTimeProvider time, TimeSpan cost) : ICompressionHandler
    {
        public bool CanHandle(ContentType type) => inner.CanHandle(type);

        public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
        {
            time.Advance(cost);
            return inner.HandleAsync(part, cancellationToken);
        }
    }

    private sealed class ThrowingLogHandler(bool cancelled = false) : ICompressionHandler
    {
        public bool CanHandle(ContentType type) => type == ContentType.Log;

        public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken) =>
            cancelled ? throw new OperationCanceledException(cancellationToken) : throw new InvalidOperationException("boom");
    }

    private sealed class RecordingContentStore : IContentStore
    {
        public List<ContentRecord> Records { get; } = [];

        public Exception? Failure { get; init; }

        public Action? OnStore { get; init; }

        public ValueTask StoreAsync(ContentRecord record, CancellationToken cancellationToken)
        {
            OnStore?.Invoke();

            if (Failure is not null)
            {
                throw Failure;
            }

            Records.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ContentRecord?> GetAsync(string requestId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Records.FirstOrDefault(record => record.RequestId == requestId));
    }
}
