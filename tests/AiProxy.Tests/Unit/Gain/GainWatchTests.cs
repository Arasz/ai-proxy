using AiProxy.Cli;
using AiProxy.Core.Gain;

namespace AiProxy.Tests.Unit.Gain;

/// <summary>The live view redraws only when what it shows changed.</summary>
public sealed class GainWatchTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-02T12:00Z");

    private static GainReport Report(params GainHistoryEntry[] requests) => GainReportBuilder.Build(new GainQuery(), At, requests, [], []);

    private static GainHistoryEntry Entry(string id, long saved) =>
        new(id, At, "/p", "anthropic", "POST", "log", "tool_result", "content", new GainTotals(1, 100, 50, 100, 50, 30, 20, saved));

    [Fact]
    public void A_report_that_differs_only_in_its_generated_time_is_not_a_change()
    {
        var shown = Report(Entry("a", 10));

        Assert.False(GainWatch.Changed(shown, Report(Entry("a", 10)) with { GeneratedAt = At.AddSeconds(2) }));
    }

    [Fact]
    public void A_new_request_or_a_different_total_is_a_change()
    {
        var shown = Report(Entry("a", 10));

        Assert.True(GainWatch.Changed(shown, Report(Entry("a", 10), Entry("b", 10))));
        Assert.True(GainWatch.Changed(shown, Report(Entry("a", 11))));
    }
}
