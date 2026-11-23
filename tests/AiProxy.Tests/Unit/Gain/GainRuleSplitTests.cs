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

    [Theory]
    [InlineData("a,a,b", new[] { "a", "b" })]
    [InlineData(",a,,b,", new[] { "a", "b" })]
    [InlineData(",,", new[] { ",," })]
    [InlineData("", new[] { "" })]
    [InlineData("a,b,c,d,e,f,g,h,i,j,k,l,m,n,o,p,q,a,r", new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r" })]
    public void A_rule_path_counts_each_distinct_member_once_and_a_path_without_members_as_itself(string path, string[] expected)
    {
        var rows = GainReportBuilder.Attribute(GainLabels.Dimensions.Rule, [new GainAttribution(GainLabels.Dimensions.Rule, path, new GainTotals(1, EstimatedSavedTokens: 3))]);

        Assert.Equal(expected, rows.Select(static row => row.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(rows, static row => Assert.Equal(new GainTotals(1, EstimatedSavedTokens: 3), row.Totals));
    }

    [Fact]
    public void Attributing_a_ledger_sized_set_of_rule_rows_allocates_little_more_than_the_result()
    {
        string[] members = ["prose.dedupe", "below-minimum", "sourcecode.blank-lines", "log.signal-filter", "markdown.strip-comments", "json.compact", "xml.compact", "ccr.preview"];
        var rows = Enumerable.Range(0, 260)
            .Select(i => new GainAttribution(GainLabels.Dimensions.Rule, string.Join(',', members.Take(1 + i % members.Length).Skip(Math.Min(i % 3, i % members.Length))), new GainTotals(1, i, i, i, i, i, i, i)))
            .ToArray();
        GainReportBuilder.Attribute(GainLabels.Dimensions.Rule, rows);

        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = GainReportBuilder.Attribute(GainLabels.Dimensions.Rule, rows);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        Assert.Equal(members.Length, result.Count);
        Assert.True(allocated < 16_000, $"allocated {allocated:N0} bytes");
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
