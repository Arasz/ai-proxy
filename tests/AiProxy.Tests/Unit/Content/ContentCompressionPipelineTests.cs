using System.Text;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;
using AiProxy.Core.Providers;

namespace AiProxy.Tests.Unit.Content;

/// <summary>
/// The pipeline, stated as behaviour: the tool output is smaller, the rest of the body is
/// exactly what the client sent, and the record says what happened.
/// </summary>
public class ContentCompressionPipelineTests
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
        CompressionOptions? options = null)
    {
        var recording = store as RecordingContentStore ?? new RecordingContentStore();
        var router = new ContentRouter(
            [
                new LogCompressionHandler(),
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
            options ?? CompressionOptions.Default);

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
        var options = CompressionOptions.Default with
        {
            MinimumLengths = new Dictionary<ContentType, int> { [ContentType.Log] = 10_000 },
        };
        var (pipeline, _) = Build(options: options);
        var body = Encoding.UTF8.GetBytes(Body(ToolOutput));

        var result = await pipeline.CompressAsync(
            new CompressionRequest(body, "application/json", Dialect.OpenAi),
            CancellationToken.None);

        Assert.False(result.Changed);
    }

    private sealed class RecordingContentStore : IContentStore
    {
        public List<ContentRecord> Records { get; } = [];

        public Exception? Failure { get; init; }

        public ValueTask StoreAsync(ContentRecord record, CancellationToken cancellationToken)
        {
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
