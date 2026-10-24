using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>
/// The log reducer, stated as behaviour: which lines survive, and which facts come back.
/// </summary>
public sealed class LogCompressionHandlerTests
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

    private static string[] Lines(string reduced) =>
        reduced.Split('\n').Where(line => !line.StartsWith("[retained identifiers]", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task The_line_that_carries_the_failure_survives_even_at_info_level()
    {
        var result = await Handler.HandleAsync(Part(Log), CancellationToken.None);

        Assert.Contains("status=503", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_that_repeats_an_earlier_shape_keeps_only_what_changed()
    {
        var result = await Handler.HandleAsync(Part(Log), CancellationToken.None);

        Assert.Equal(1, Lines(result.Text).Count(line => line.Contains("nothing to see", StringComparison.Ordinal)));
        Assert.Contains("10:00:03", Lines(result.Text));
    }

    [Fact]
    public async Task The_first_line_of_a_shape_is_kept_whole()
    {
        var result = await Handler.HandleAsync(Part(Log), CancellationToken.None);

        Assert.Contains("2026-03-12 10:00:02 INFO worker=api-1 nothing to see", Lines(result.Text));
    }

    [Fact]
    public async Task The_values_that_changed_stay_together_with_their_keys()
    {
        // The scored question asks for one field of the row picked by another field, so the
        // two values must survive on the same line, each next to its key.
        var text = string.Join("\n",
        [
            "2026-10-23T18:25:25.508537Z  INFO ccr: compressed session=cdb66ea2 tokens_compressed=819 tokens_in_context=283 blocks_compressed=1",
            "2026-10-23T18:25:25.521939Z  INFO ccr: compressed session=246079e5 tokens_compressed=1419 tokens_in_context=1031 blocks_compressed=1",
            "2026-10-23T18:25:25.555186Z  INFO ccr: compressed session=ebd546fe tokens_compressed=2443 tokens_in_context=2033 blocks_compressed=2",
        ]);

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        Assert.Contains("2026-10-23T18:25:25.521939Z session=246079e5 tokens_compressed=1419 tokens_in_context=1031", Lines(result.Text));
        Assert.Contains("2026-10-23T18:25:25.555186Z session=ebd546fe tokens_compressed=2443 tokens_in_context=2033 blocks_compressed=2", Lines(result.Text));
    }

    [Fact]
    public async Task A_line_with_another_shape_is_kept_whole()
    {
        var text = string.Join("\n",
        [
            "2026-10-23T18:25:09Z INFO rtk_proxy: pipeline stages=[\"normalize\", \"ccr\"]",
            "2026-10-23T18:25:09Z INFO rtk_proxy: rtk-proxy listening bind=\"127.0.0.1:18080\"",
            "2026-10-23T18:25:10Z INFO rtk_proxy: rtk-proxy listening bind=\"127.0.0.1:18081\" again",
        ]);

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        Assert.Equal(text.Split('\n'), Lines(result.Text));
    }

    [Fact]
    public async Task A_line_with_other_keys_is_not_shortened_against_an_unrelated_line()
    {
        var text = string.Join("\n",
        [
            "10:00:00 INFO worker=api-1 status=200",
            "10:00:01 INFO worker=api-1 retries=200",
        ]);

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        Assert.Equal(text.Split('\n'), Lines(result.Text));
    }

    [Fact]
    public async Task A_value_without_a_key_is_compared_too()
    {
        var text = string.Join("\n",
        [
            "10:00:00 worker api-1 took 41 ms",
            "10:00:01 worker api-1 took 12 ms",
        ]);

        var result = await Handler.HandleAsync(Part(text), CancellationToken.None);

        Assert.Contains("10:00:01 12", Lines(result.Text));
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
    public async Task Every_line_the_reduction_keeps_is_words_of_one_input_line_in_order()
    {
        var result = await Handler.HandleAsync(Part(Log), CancellationToken.None);

        foreach (var line in Lines(result.Text))
        {
            Assert.Contains(Log.Split('\n'), input => OrderedSubsequence.Holds(line, input) && line.Split(' ').All(input.Split(' ').Contains));
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
