using AiProxy.Core.Gain;

namespace AiProxy.Tests.Unit.Gain;

/// <summary>Net tokens and percentages over fixed in-memory ledgers.</summary>
public sealed class GainNetTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-30T12:00:00Z");

    [Fact]
    public void Net_subtracts_retrieved_and_tool_tokens_from_saved_and_stays_positive_while_saved_is_larger()
    {
        var totals = new GainTotals(1, EstimatedOriginalTokens: 10_000, EstimatedSavedTokens: 1_000, EstimatedRetrievedTokens: 300, EstimatedToolTokens: 50);

        Assert.Equal(650, totals.EstimatedNetTokens);
        Assert.Equal(10.0m, totals.SavedPercent);
        Assert.Equal(6.5m, totals.NetPercent);
    }

    [Fact]
    public void Net_turns_negative_when_retrieval_and_tool_cost_more_than_was_saved()
    {
        var totals = new GainTotals(1, EstimatedOriginalTokens: 1_000, EstimatedSavedTokens: 100, EstimatedRetrievedTokens: 80, EstimatedToolTokens: 50);

        Assert.Equal(-30, totals.EstimatedNetTokens);
        Assert.Equal(10.0m, totals.SavedPercent);
        Assert.Equal(-3.0m, totals.NetPercent);
    }

    [Fact]
    public void Percentages_are_zero_when_nothing_was_original()
    {
        var totals = new GainTotals();

        Assert.Equal(0m, totals.SavedPercent);
        Assert.Equal(0m, totals.NetPercent);
    }

    [Fact]
    public void A_built_report_sums_tool_cost_with_requests_and_retrieval_with_recalls()
    {
        var request = new GainHistoryEntry("r1", Now, "/p", "anthropic", "POST", "ccr.tool", GainLabels.Unknown, GainLabels.Pipelines.Ccr,
            new GainTotals(1, EstimatedOriginalTokens: 4_000, EstimatedForwardedTokens: 3_000, EstimatedSavedTokens: 1_000, EstimatedToolTokens: 200));
        var recall = new GainRecallEvent(
            new GainRecall(Now, "preview", "/p", GainLabels.Attributions.Caller, "whole", 1_000, 400, null),
            new GainPath("POST", "ccr.tool", GainLabels.Layers.Retrieval, GainLabels.Pipelines.Ccr));

        var report = GainReportBuilder.Build(new GainQuery(), Now, [request], [], [recall]);

        Assert.Equal(200, report.Summary.EstimatedToolTokens);
        Assert.Equal(400, report.Summary.EstimatedRetrievedTokens);
        Assert.Equal(400, report.Summary.EstimatedNetTokens);
        Assert.Equal(25.0m, report.Summary.SavedPercent);
        Assert.Equal(10.0m, report.Summary.NetPercent);
        Assert.Equal(200, Assert.Single(report.Daily).Totals.EstimatedToolTokens);
    }

    [Fact]
    public void A_request_with_a_tool_keeps_the_tool_out_of_saved_and_reports_it_separately()
    {
        var totals = GainReportBuilder.RequestTotals(2_890, 2_890 + 289, 2_890, 2_890 + 289, 289);

        Assert.Equal(0, totals.EstimatedSavedTokens);
        Assert.Equal(100, totals.EstimatedToolTokens);
        Assert.Equal(1_100, totals.EstimatedForwardedTokens);
        Assert.Equal(-100, totals.EstimatedNetTokens);
    }
}
