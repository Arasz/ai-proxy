using AiProxy.Core.Content.Ccr;
using AiProxy.Tests.Unit.Content.Ccr;
using System.Text;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;
using AiProxy.Core.Providers;

namespace AiProxy.Tests.Unit.Content;

/// <summary>
/// The pipeline with file reads, stated as behaviour: the path of the call that read a file
/// reaches the reducer that runs on its result.
/// </summary>
public sealed class ContentCompressionPipelineFilePathTests
{
    [Fact]
    public async Task Each_reducer_sees_the_path_of_the_call_that_read_its_file_when_the_results_come_reversed()
    {
        var handler = new RecordingSourceCodeHandler();
        var pipeline = new ContentCompressionPipeline(
            new ToolOutputRegionFinder(),
            new ContentDecompositor(),
            new CompressionExecutor(new ContentRouter([handler], new PassThroughCompressionHandler())),
            new ContentCompositor(),
            new NullContentStore(),
            CompressionOptions.WithMinimum(1),
            TimeProvider.System,
            new CcrPreviewer(new MemoryOriginalStore(), () => new CcrPreviewBuilder(new CcrPreviewOverlay()), CcrOptions.Default));

        var body = """
            {"messages":[
              {"role":"assistant","content":[
                {"type":"tool_use","id":"call-a","name":"Read","input":{"file_path":"src/A.cs"}},
                {"type":"tool_use","id":"call-b","name":"Read","input":{"file_path":"src/b.py"}}]},
              {"role":"user","content":[
                {"type":"tool_result","tool_use_id":"call-b","content":"text of b"},
                {"type":"tool_result","tool_use_id":"call-a","content":"text of a"}]}]}
            """;

        await pipeline.CompressAsync(
            new CompressionRequest(Encoding.UTF8.GetBytes(body), "application/json", Dialect.Anthropic),
            CancellationToken.None);

        Assert.Equal([("text of b", "src/b.py"), ("text of a", "src/A.cs")], handler.Seen);
    }

    [Fact]
    public async Task A_lone_surrogate_in_another_call_still_lets_the_request_compress()
    {
        var handler = new RecordingSourceCodeHandler();
        var pipeline = new ContentCompressionPipeline(
            new ToolOutputRegionFinder(),
            new ContentDecompositor(),
            new CompressionExecutor(new ContentRouter([handler], new PassThroughCompressionHandler())),
            new ContentCompositor(),
            new NullContentStore(),
            CompressionOptions.WithMinimum(1),
            TimeProvider.System,
            new CcrPreviewer(new MemoryOriginalStore(), () => new CcrPreviewBuilder(new CcrPreviewOverlay()), CcrOptions.Default));

        var body = """
            {"messages":[
              {"role":"assistant","tool_calls":[
                {"id":"call-x","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"x\ud800y\"}"}},
                {"id":"call-a","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"src/A.cs\"}"}}]},
              {"role":"tool","tool_call_id":"call-a","content":"text of a"}]}
            """;

        var result = await pipeline.CompressAsync(
            new CompressionRequest(Encoding.UTF8.GetBytes(body), "application/json", Dialect.OpenAi),
            CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal([("text of a", "src/A.cs")], handler.Seen);
    }

    private sealed class RecordingSourceCodeHandler : ICompressionHandler
    {
        public List<(string Text, string? FilePath)> Seen { get; } = [];

        public bool CanHandle(ContentType type) => type == ContentType.SourceCode;

        public ValueTask<HandledContent> HandleAsync(ContentPart part, CancellationToken cancellationToken)
        {
            Seen.Add((part.Text, part.FilePath));
            return ValueTask.FromResult(new HandledContent(part.Text[..1], "recording"));
        }
    }

    private sealed class NullContentStore : IContentStore
    {
        public ValueTask StoreAsync(ContentRecord record, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<ContentRecord?> GetAsync(string requestId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ContentRecord?>(null);
    }
}
