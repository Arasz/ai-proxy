using System.Text;
using System.Text.Json;
using AiProxy.Code;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Content.Handlers;
using AiProxy.Core.Providers;

namespace AiProxy.Tests.Unit.Content.Ccr;

/// <summary>A code view leaves the proxy only when the original it names is kept under the id in its hint.</summary>
public sealed class ContentCompressionPipelineCodeViewTests
{
    private const string First =
        "using System;\nusing System.Collections.Generic;\n\n/// <summary>The first file's type; this doc comment is long enough that the view, hint included, is smaller than the file.</summary>\npublic sealed class Example\n{\n    public int Value => 1;\n}\n";

    private const string Second =
        "using System.Linq;\nusing System.Collections.Generic;\n\n/// <summary>The second file's type; this doc comment is also long enough that the view is smaller than the file.</summary>\npublic sealed class Example\n{\n    public int Value => 2;\n}\n";

    private static byte[] Body(params ReadFile[] files) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        messages = new object[]
        {
            new
            {
                role = "assistant",
                content = files.Select((file, index) => new { type = "tool_use", id = $"call-{index}", name = "Read", input = new { file_path = file.Path } }).ToArray(),
            },
            new
            {
                role = "user",
                content = files.Select((file, index) => new { type = "tool_result", tool_use_id = $"call-{index}", content = file.Text }).ToArray(),
            },
        },
    }));

    private static ContentCompressionPipeline Build(MemoryOriginalStore store) => new(
        new ToolOutputRegionFinder(),
        new ContentDecompositor(),
        new CompressionExecutor(new ContentRouter([new SourceCodeCompressionHandler([new CSharpCodeLexer()])], new PassThroughCompressionHandler())),
        new ContentCompositor(),
        new NullContentStore(),
        CompressionOptions.WithMinimum(1),
        TimeProvider.System,
        new CcrPreviewer(store, () => new CcrPreviewBuilder(new CcrPreviewOverlay()), CcrOptions.Default));

    private static Task<CompressionResult> CompressAsync(MemoryOriginalStore store, CcrMode mode, params ReadFile[] files) =>
        Build(store).CompressAsync(new CompressionRequest(Body(files), "application/json", Dialect.Anthropic) { Ccr = mode }, CancellationToken.None).AsTask();

    private static string[] ToolResults(CompressionResult result)
    {
        using var document = JsonDocument.Parse(result.Body);
        return document.RootElement.GetProperty("messages")[1].GetProperty("content").EnumerateArray()
            .Select(block => block.GetProperty("content").GetString() ?? string.Empty)
            .ToArray();
    }

    [Fact]
    public async Task Two_files_with_one_name_each_name_their_own_original_and_both_come_back_exact()
    {
        var store = new MemoryOriginalStore();

        var result = await CompressAsync(store, CcrMode.Stateless, new ReadFile("src/a/Example.cs", First), new ReadFile("src/b/Example.cs", Second));

        var sent = ToolResults(result);
        Assert.EndsWith(CcrView.Hint(CcrView.IdOf(First)), sent[0], StringComparison.Ordinal);
        Assert.EndsWith(CcrView.Hint(CcrView.IdOf(Second)), sent[1], StringComparison.Ordinal);
        Assert.Equal(First, result.Originals[CcrView.IdOf(First)]);
        Assert.Equal(Second, result.Originals[CcrView.IdOf(Second)]);
        Assert.Empty(store.Originals);
    }

    [Fact]
    public async Task A_thread_keeps_the_original_in_the_store_before_the_view_is_sent()
    {
        var store = new MemoryOriginalStore();

        var result = await CompressAsync(store, CcrMode.Thread, new ReadFile("src/Example.cs", First));

        Assert.EndsWith(CcrView.Hint(CcrView.IdOf(First)), ToolResults(result)[0], StringComparison.Ordinal);
        Assert.Equal(First, store.Originals[CcrView.IdOf(First)]);
        Assert.Equal(First, result.Originals[CcrView.IdOf(First)]);
    }

    [Fact]
    public async Task A_thread_whose_store_fails_sends_the_original_and_reports_the_failure()
    {
        var store = new MemoryOriginalStore { Failure = new IOException("disk full") };

        var result = await CompressAsync(store, CcrMode.Thread, new ReadFile("src/Example.cs", First));

        Assert.DoesNotContain(CcrView.Marker, Encoding.UTF8.GetString(result.Body.Span), StringComparison.Ordinal);
        Assert.Equal(First.TrimEnd('\n'), ToolResults(result)[0].TrimEnd('\n'));
        Assert.Empty(result.Originals);
        var failure = Assert.Single(result.PartFailures);
        Assert.Equal(ContentCompressionPipeline.OriginalNotKeptPath, failure.ContentType);
        Assert.IsType<IOException>(failure.Exception);
    }

    [Fact]
    public async Task With_ccr_off_no_view_leaves_the_proxy()
    {
        var store = new MemoryOriginalStore();

        var result = await CompressAsync(store, CcrMode.Off, new ReadFile("src/Example.cs", First));

        Assert.DoesNotContain(CcrView.Marker, Encoding.UTF8.GetString(result.Body.Span), StringComparison.Ordinal);
        Assert.Empty(result.Originals);
        Assert.Empty(result.PartFailures);
    }

    [Fact]
    public async Task A_file_large_enough_for_a_preview_sends_the_preview_under_the_same_id()
    {
        var large = "using System;\n\npublic sealed class Large\n{\n"
            + string.Concat(Enumerable.Range(1, 400).Select(index => $"    // Member {index} keeps this comment.\n    public int Value{index} => {index};\n"))
            + "}\n";

        var result = await CompressAsync(new MemoryOriginalStore(), CcrMode.Stateless, new ReadFile("src/Large.cs", large));

        var sent = ToolResults(result)[0];
        Assert.StartsWith(CcrPreview.Marker + CcrView.IdOf(large), sent, StringComparison.Ordinal);
        Assert.DoesNotContain(CcrView.Marker, sent, StringComparison.Ordinal);
        var (id, original) = Assert.Single(result.Originals);
        Assert.Equal(CcrView.IdOf(large), id);
        Assert.Equal(large, original);
    }

    [Fact]
    public async Task The_same_file_gives_the_same_view_bytes_on_every_send()
    {
        var first = await CompressAsync(new MemoryOriginalStore(), CcrMode.Stateless, new ReadFile("src/Example.cs", First));
        var second = await CompressAsync(new MemoryOriginalStore(), CcrMode.Stateless, new ReadFile("src/Example.cs", First));

        Assert.Equal(first.Body.ToArray(), second.Body.ToArray());
    }

    private sealed record ReadFile(string Path, string Text);

    private sealed class NullContentStore : IContentStore
    {
        public ValueTask StoreAsync(ContentRecord record, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<ContentRecord?> GetAsync(string requestId, CancellationToken cancellationToken) => ValueTask.FromResult<ContentRecord?>(null);
    }
}
