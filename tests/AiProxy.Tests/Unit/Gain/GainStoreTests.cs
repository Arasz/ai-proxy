using AiProxy.Core.Gain;
using AiProxy.Storage;
using AiProxy.Tests.Integration;
using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests.Unit.Gain;

public sealed class GainStoreTests
{
    [Fact]
    public async Task Filters_calendar_buckets_signed_estimates_and_restart_reconcile_without_duplicate_rule_savings()
    {
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-02T12:00:00Z"));
        var options = new GainStoreOptions(Path.Combine(directory.Path, "gain.db"));
        var store = new SqliteGainStore(options, clock);
        var first = Entry("a", "2026-11-01T01:00:00+02:00", "/a", "json,log", 289, 578);
        var second = Entry("b", "2026-11-01T12:00:00Z", "/b", "json", 578, 289);
        var third = Entry("c", "2026-11-02T00:00:00Z", "/a", "json,log", 578, 289);
        await store.RecordAsync(first, [new(first.At,"a","/a","compression","ExampleFailure")], TestContext.Current.CancellationToken);
        await store.RecordAsync(second, [], TestContext.Current.CancellationToken);
        await store.RecordAsync(third, [], TestContext.Current.CancellationToken);
        await store.RecallAsync(new GainRecall(first.At,"preview","/a","caller","whole",289,100,null),"anthropic","POST","json,log","tool_result","content",TestContext.Current.CancellationToken);
        store = new SqliteGainStore(options, clock);
        var query = new GainQuery(Project:"/a", To:DateTimeOffset.Parse("2026-11-02T00:00Z"), Rule:"json,log", HistoryLimit:1);
        var report = await store.QueryAsync(query, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Summary.Requests);
        Assert.Equal(-100, report.Summary.EstimatedSavedTokens);
        Assert.Equal(1, report.Summary.Recalls);
        Assert.Equal("ExampleFailure", Assert.Single(report.Failures).Error);
        Assert.Equal(DateTimeOffset.Parse("2026-10-31T00:00:00Z"), Assert.Single(report.Daily).Start);
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T00:00:00Z"), Assert.Single(report.Weekly).Start);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00Z"), Assert.Single(report.Monthly).Start);
        Assert.Equal("a", Assert.Single(report.History).Id);
        foreach (var dimension in report.Attributions.GroupBy(static row => row.Dimension))
        {
            Assert.Equal(report.Summary.EstimatedSavedTokens, dimension.Sum(static row => row.Totals.EstimatedSavedTokens));
        }
        Assert.Equal("json,log", Assert.Single(report.Attributions, static row => row.Dimension == "rule").Name);
        var injected = await store.QueryAsync(new GainQuery(Project:"' OR 1=1 --"), TestContext.Current.CancellationToken);
        Assert.Equal(0, injected.Summary.Requests);
        Assert.Equal(2.89m, report.Measurement.CharactersPerToken);
        Assert.False(report.Measurement.NetSavingsAvailable);
    }

    [Fact]
    public async Task Filtered_reset_preserves_other_projects_newer_events_and_original_database()
    {
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-01T12:00Z"));
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path,"gain.db")), clock);
        await store.RecordAsync(Entry("old","2026-11-01T10:00Z","/a","log",289,100),[],TestContext.Current.CancellationToken);
        await store.RecordAsync(Entry("new","2026-11-01T13:00Z","/a","log",289,100),[],TestContext.Current.CancellationToken);
        await store.RecordAsync(Entry("other","2026-11-01T10:00Z","/b","log",289,100),[],TestContext.Current.CancellationToken);
        var result = await store.ResetAsync(new GainQuery(Project:"/a"),TestContext.Current.CancellationToken);
        Assert.Equal(1,result.Removed);
        Assert.Equal(clock.GetUtcNow(),result.Cutoff);
        var report = await store.QueryAsync(new GainQuery(),TestContext.Current.CancellationToken);
        Assert.Equal(2,report.Summary.Requests);
        Assert.DoesNotContain(report.History, row => row.Id == "old");
    }

    private static GainHistoryEntry Entry(string id,string at,string project,string rule,long original,long forwarded) =>
        new(id,DateTimeOffset.Parse(at),project,"anthropic","POST",rule,"tool_result","content",
            GainReportBuilder.RequestTotals(original,forwarded,original,forwarded));
}
