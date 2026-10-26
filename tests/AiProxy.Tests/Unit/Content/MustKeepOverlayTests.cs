using AiProxy.Core.Content;

namespace AiProxy.Tests.Unit.Content;

/// <summary>
///     The overlay that re-attaches what a reduction dropped, stated as behaviour: which facts
///     must survive, and how much the overlay is allowed to cost.
/// </summary>
public sealed class MustKeepOverlayTests
{
    [Fact]
    public void A_failure_verdict_the_reduction_dropped_comes_back()
    {
        var original = "build step\nFAILED\nthe next step";
        var reduced = "build step\nthe next step";

        var result = MustKeepOverlay.Apply(reduced, original);

        Assert.Contains("FAILED", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lowercase_failure_verdict_comes_back_too()
    {
        // The measured overlay catches ERROR and FATAL only because they are capitalised.
        // A lowercase verdict is the hole the record names, and this is the rule that closes it.
        var original = "worker-3 failed\nnext line";
        var reduced = "next line";

        var result = MustKeepOverlay.Apply(reduced, original);

        Assert.Contains("failed", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exit_status_the_reduction_dropped_comes_back()
    {
        var original = "bash: build failed\nexit code 2";
        var reduced = "bash: build failed";

        var result = MustKeepOverlay.Apply(reduced, original);

        Assert.Contains("exit code 2", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lowercase_state_transition_comes_back()
    {
        var original = "worker-3 retried after 2 attempts\nnothing else";
        var reduced = "nothing else";

        var result = MustKeepOverlay.Apply(reduced, original);

        Assert.Contains("retried", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absolute_path_comes_back()
    {
        var original = "could not open /var/Log/App.log\nnothing else";
        var reduced = "nothing else";

        var result = MustKeepOverlay.Apply(reduced, original);

        Assert.Contains("/var/Log/App.log", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_directive_comes_back()
    {
        var original = "must-keep: the trace id\nnothing else";
        var reduced = "nothing else";

        var result = MustKeepOverlay.Apply(reduced, original);

        Assert.Contains("must-keep", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_the_reduction_kept_is_not_repeated()
    {
        var original = "the path /var/app.log is gone";
        var reduced = "the path /var/app.log is gone";

        var result = MustKeepOverlay.Apply(reduced, original);

        Assert.Equal(reduced, result.Text);
        Assert.Equal(0, result.RetainedTokens);
    }

    [Fact]
    public void A_reduction_that_dropped_nothing_gets_no_block()
    {
        var original = "one line\nanother line";
        var reduced = "one line\nanother line";

        var result = MustKeepOverlay.Apply(reduced, original);

        Assert.Equal(reduced, result.Text);
        Assert.False(result.CeilingReached);
    }

    [Fact]
    public void The_reduced_text_is_never_rewritten()
    {
        var original = "the id 8842 failed at /srv/app/pool.py line=214";
        var reduced = "the id failed at";

        var result = MustKeepOverlay.Apply(reduced, original);

        Assert.StartsWith(reduced, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_overlay_stops_at_its_ceiling()
    {
        // A long reduction and a short original full of tokens: the overlay may not cost
        // more than five percent of what the reduction saved.
        var original = string.Join(
            " ",
            Enumerable.Range(0, 400).Select(index => $"/srv/app/file{index}.cs"));
        var reduced = new string('x', 4000);

        var result = MustKeepOverlay.Apply(reduced, original);

        var overhead = result.Text.Length - reduced.Length;
        var ceiling = Math.Max(
            MustKeepOverlay.MinimumOverhead,
            (int)(MustKeepOverlay.MaximumOverhead * reduced.Length));
        Assert.True(
            overhead <= ceiling,
            $"the overlay added {overhead} characters to a {reduced.Length} character reduction");
        Assert.True(result.CeilingReached);
    }

    [Fact]
    public void A_decision_survives_the_ceiling()
    {
        var original = $"{string.Join(
            " ",
            Enumerable.Range(0, 400).Select(index => $"/srv/app/file{index}.cs"))} FAILED";
        var reduced = new string('x', 4000);

        var result = MustKeepOverlay.Apply(reduced, original);

        Assert.Contains("FAILED", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_overlay_says_how_many_tokens_it_kept()
    {
        var original = "the id 8842 failed";
        var reduced = "the id failed";

        var result = MustKeepOverlay.Apply(reduced, original);

        Assert.Equal(1, result.RetainedTokens);
    }
}