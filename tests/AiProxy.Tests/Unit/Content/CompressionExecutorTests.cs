using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content;

/// <summary>The executor, stated as behaviour: it runs the handler and measures what it did.</summary>
public class CompressionExecutorTests
{
    private static readonly CompressionExecutor Executor = new(
        new ContentRouter(
            [
                new LogCompressionHandler(),
            ],
            new PassThroughCompressionHandler()));

    [Fact]
    public async Task The_executor_reports_the_path_the_handler_took()
    {
        var part = new ContentPart("request", 1, ContentType.Log, "2026-03-12 10:00:00 INFO a\n2026-03-12 10:00:01 INFO b");

        var result = await Executor.ExecuteAsync(part, CancellationToken.None);

        Assert.Equal("log.signal-filter", result.Path);
    }

    [Fact]
    public async Task The_executor_reports_the_ratio_of_the_reduction()
    {
        var text = string.Join("\n", Enumerable.Range(0, 10).Select(index => $"2026-03-12 10:00:00 INFO line {index}"));
        var part = new ContentPart("request", 1, ContentType.Log, text);

        var result = await Executor.ExecuteAsync(part, CancellationToken.None);

        Assert.Equal((double)result.Text.Length / text.Length, result.Ratio, 6);
    }

    [Fact]
    public async Task The_executor_keeps_the_part_it_reduced()
    {
        var part = new ContentPart("request", 7, ContentType.Log, "2026-03-12 10:00:00 INFO a\n2026-03-12 10:00:01 INFO b");

        var result = await Executor.ExecuteAsync(part, CancellationToken.None);

        Assert.Equal(7, result.ChunkId);
        Assert.Equal(ContentType.Log, result.Type);
    }
}
