using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>
/// The log reducer, stated as behaviour: which lines survive, and which facts come back.
/// </summary>
public class LogCompressionHandlerTests
{
    private const string Log = """
        2026-03-12 10:00:00 INFO worker=api-1 request started
        2026-03-12 10:00:01 INFO worker=api-1 request failed status=503 dur_ms=3110
        2026-03-12 10:00:02 INFO worker=api-1 nothing to see
        2026-03-12 10:00:03 INFO worker=api-1 nothing to see
        2026-03-12 10:00:04 ERROR worker=api-2 boom
        2026-03-12 10:00:05 INFO worker=api-2 the last line
        """;

    private static readonly LogCompressionHandler Handler = new();

    private static ContentPart Part(string text) => new("request", 1, ContentType.Log, text);

    [Fact]
    public async Task The_line_that_carries_the_failure_survives_even_at_info_level()
    {
        var result = await Handler.HandleAsync(Part(Log), CancellationToken.None);

        Assert.Contains("status=503", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_without_a_signal_and_outside_the_anchors_is_dropped()
    {
        var result = await Handler.HandleAsync(Part(Log), CancellationToken.None);

        Assert.DoesNotContain("nothing to see", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_two_lines_are_anchors_and_survive()
    {
        var result = await Handler.HandleAsync(Part(Log), CancellationToken.None);

        Assert.Contains("request started", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_at_error_level_survives_even_when_its_message_carries_no_signal()
    {
        // The measured rule keeps a line on its level as well as on its message, and the
        // level is the only reason this line survives: "boom" says nothing.
        var text = string.Join("\n",
        [
            "2026-03-12 10:00:00 INFO worker=api-1 nothing to see",
            "2026-03-12 10:00:01 INFO worker=api-1 nothing to see",
            "2026-03-12 10:00:02 INFO worker=api-1 nothing to see",
            "2026-03-12 10:00:03 ERROR worker=api-2 boom",
            "2026-03-12 10:00:04 INFO worker=api-1 nothing to see",
            "2026-03-12 10:00:05 INFO worker=api-1 nothing to see",
            "2026-03-12 10:00:06 INFO worker=api-1 nothing to see",
        ]);

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        Assert.Contains("boom", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_last_two_lines_are_anchors_and_survive()
    {
        var text = string.Join("\n",
        [
            "2026-03-12 10:00:00 INFO worker=api-1 nothing to see",
            "2026-03-12 10:00:01 INFO worker=api-1 nothing to see",
            "2026-03-12 10:00:02 INFO worker=api-1 nothing to see",
            "2026-03-12 10:00:03 INFO worker=api-1 nothing to see",
            "2026-03-12 10:00:04 INFO worker=api-1 the second to last line",
            "2026-03-12 10:00:05 INFO worker=api-1 the last line",
        ]);

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        Assert.Contains("the last line", result.Text, StringComparison.Ordinal);
        Assert.Contains("the second to last line", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repeated_line_is_dropped_after_the_first()
    {
        // The measured rule says a line repeated on every request is the cheapest byte to lose.
        var text = string.Join("\n", Enumerable.Repeat("2026-03-12 10:00:00 INFO worker=api-1 same", 6));

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        Assert.Equal(1, result.Text.Split('\n').Count(line => line.Contains("same", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_handler_says_which_path_it_took()
    {
        var result = await Handler.HandleAsync(Part(Log), CancellationToken.None);

        Assert.Equal("log.signal-filter", result.Path);
    }

    [Fact]
    public async Task Every_line_the_reduction_keeps_is_a_line_of_the_input()
    {
        var result = await Handler.HandleAsync(Part(Log), CancellationToken.None);

        var kept = result.Text.Split('\n').Where(line => !line.StartsWith("[retained identifiers]", StringComparison.Ordinal));
        foreach (var line in kept)
        {
            Assert.Contains(line, Log.Split('\n'), StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task The_reduction_is_an_ordered_subsequence_of_the_input()
    {
        var result = await Handler.HandleAsync(Part(Log), CancellationToken.None);

        OrderedSubsequence.AssertHolds(result.Text, Log);
    }

    [Fact]
    public async Task The_handler_claims_the_log_kind_and_nothing_else()
    {
        Assert.True(Handler.CanHandle(ContentType.Log));
        Assert.False(Handler.CanHandle(ContentType.Json));
        Assert.False(Handler.CanHandle(ContentType.Prose));
    }
}
