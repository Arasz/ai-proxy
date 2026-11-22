using AiProxy.Core.Gain;
using AiProxy.Storage;
using AiProxy.Tests.Integration;
using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests.Unit.Gain;

public sealed class GainRuleSplitTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-02T12:00Z");

    private static GainHistoryEntry Entry(string id, string rule, long saved = 10) =>
        new(id, At, "/p", "anthropic", "POST", rule, "tool_result", "content",
            new GainTotals(1, 100, 50, 100, 50, 30, 20, saved));

    [Fact]
    public void A_request_with_several_rules_counts_once_under_each_rule()
    {
        var report = GainReportBuilder.Build(new GainQuery(), At, [Entry("r1", "a,b")], [], []);

        var rules = report.Attributions.Where(static row => row.Dimension == "rule").ToArray();
        Assert.Equal(["a", "b"], rules.Select(static row => row.Name).Order().ToArray());
        Assert.All(rules, static row => Assert.Equal(1, row.Totals.Requests));
        Assert.DoesNotContain(rules, static row => row.Name.Contains(','));
    }

    [Fact]
    public void Rule_rows_are_sorted_by_saved_tokens_descending_then_name()
    {
        var report = GainReportBuilder.Build(new GainQuery(), At,
            [Entry("r1", "b", 5), Entry("r2", "a", 5), Entry("r3", "c", 50)], [], []);

        var names = report.Attributions.Where(static row => row.Dimension == "rule").Select(static row => row.Name);
        Assert.Equal(["c", "a", "b"], names);
    }

    [Fact]
    public async Task Rule_filter_matches_a_member_of_the_stored_path_and_not_a_longer_name()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path, "gain.db")), new FakeTimeProvider(At));
        await store.RecordAsync(Entry("combined", "below-minimum,json.compact,ccr.preview"), [], TestContext.Current.CancellationToken);
        await store.RecordAsync(Entry("longer", "json.compactx"), [], TestContext.Current.CancellationToken);

        var report = await store.QueryAsync(new GainQuery(Rule: "json.compact"), TestContext.Current.CancellationToken);

        Assert.Equal("combined", Assert.Single(report.History).Id);
    }

    [Fact]
    public async Task Stored_rule_attributions_split_a_combined_path_into_members()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path, "gain.db")), new FakeTimeProvider(At));
        await store.RecordAsync(Entry("combined", "a,b"), [], TestContext.Current.CancellationToken);

        var report = await store.QueryAsync(new GainQuery(), TestContext.Current.CancellationToken);

        var names = report.Attributions.Where(static row => row.Dimension == "rule").Select(static row => row.Name).Order();
        Assert.Equal(["a", "b"], names);
    }
}
