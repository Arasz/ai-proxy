using System.Diagnostics;
using System.Text;
using AiProxy.Core.Gain;
using AiProxy.Storage;
using AiProxy.Tests.Integration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests.Unit.Gain;

public sealed class GainStoreTests
{
    [Fact]
    public async Task Unicode_byte_counts_remain_distinct_from_characters_in_every_aggregate_view()
    {
        using var directory = new TemporaryDirectory();
        var at = DateTimeOffset.Parse("2026-11-02T12:00Z");
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path, "gain.db")), new FakeTimeProvider(at));
        const string original = "é🙂abcdef";
        const string forwarded = "🙂a";
        var entry = new GainHistoryEntry("unicode", at, "/unicode", "anthropic", "POST", "log", "tool_result", "content",
            new GainTotals(1, original.Length, forwarded.Length, Encoding.UTF8.GetByteCount(original), Encoding.UTF8.GetByteCount(forwarded), 3, 1, 2));
        await store.RecordAsync(entry, [], TestContext.Current.CancellationToken);

        var report = await store.QueryAsync(new GainQuery(HistoryLimit: 0), TestContext.Current.CancellationToken);

        // é takes one UTF-16 code unit and two UTF-8 bytes; 🙂 takes two and four.
        var expected = new GainTotals(1, 9, 3, 12, 5, 3, 1, 2);
        Assert.Equal(expected, report.Summary);
        Assert.Equal(new GainBucket(DateTimeOffset.Parse("2026-11-02T00:00Z"), expected), Assert.Single(report.Daily));
        Assert.Equal(new GainBucket(DateTimeOffset.Parse("2026-11-02T00:00Z"), expected), Assert.Single(report.Weekly));
        Assert.Equal(new GainBucket(DateTimeOffset.Parse("2026-11-01T00:00Z"), expected), Assert.Single(report.Monthly));
        Assert.Collection(report.Attributions,
            row => Assert.Equal(new GainAttribution("method", "POST", expected), row),
            row => Assert.Equal(new GainAttribution("rule", "log", expected), row),
            row => Assert.Equal(new GainAttribution("layer", "tool_result", expected), row),
            row => Assert.Equal(new GainAttribution("pipeline", "content", expected), row));
    }

    [Theory]
    [InlineData("request", "Id", 1)]
    [InlineData("failure", "Error", 1)]
    [InlineData("recall", "PreviewId", 1)]
    [InlineData("request", "Id", 0)]
    [InlineData("failure", "Error", 0)]
    [InlineData("recall", "PreviewId", 0)]
    public async Task Excluded_detail_payloads_are_not_deserialized_but_still_contribute_to_the_report(string kind, string field, int limit)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "gain.db");
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-02T12:00Z"));
        var store = new SqliteGainStore(new GainStoreOptions(path), clock);
        var old = Entry("old", "2026-11-01T10:00Z", "/a", "log", 289, 100);
        var recent = Entry("recent", "2026-11-02T10:00Z", "/a", "log", 578, 289);
        foreach (var entry in new[] { old, recent })
        {
            await store.RecordAsync(entry, [new GainFailure(entry.At, entry.Id, entry.Project, "compression", "Failure")], TestContext.Current.CancellationToken);
            await store.RecallAsync(new GainRecall(entry.At, entry.Id, entry.Project, "caller", "whole", 289, 100, null),
                entry.Provider, entry.Method, entry.Rule, entry.Layer, entry.Pipeline, TestContext.Current.CancellationToken);
        }

        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE gain_event SET payload = json_set(payload, @field, 1) WHERE kind = @kind AND at = @at";
            command.Parameters.AddWithValue("@field", $"$.{field}");
            command.Parameters.AddWithValue("@kind", kind);
            command.Parameters.AddWithValue("@at", old.At.UtcTicks);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        var report = await store.QueryAsync(new GainQuery(HistoryLimit: limit), TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Summary.Requests);
        Assert.Equal(867, report.Summary.OriginalCharacters);
        Assert.Equal(2, report.Summary.Recalls);
        Assert.Equal(578, report.Summary.RetrievedCharacters);
        Assert.Equal(limit, report.History.Count);
        Assert.Equal(limit, report.Failures.Count);
        Assert.Equal(limit, report.Recalls.Count);
        if (limit > 0)
        {
            Assert.Equal("recent", Assert.Single(report.History).Id);
            Assert.Equal("recent", Assert.Single(report.Failures).RequestId);
            Assert.Equal("recent", Assert.Single(report.Recalls).PreviewId);
        }
    }

    [Fact]
    public async Task One_tick_before_a_utc_month_and_monday_boundary_stays_in_the_previous_buckets_with_per_request_rounding()
    {
        using var directory = new TemporaryDirectory();
        var boundary = DateTimeOffset.Parse("2026-06-01T00:00Z");
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path, "gain.db")), new FakeTimeProvider(boundary));
        var before = Entry("before", "2026-05-31T00:00Z", "/a", "log", 2, 3) with { At = boundary.AddTicks(-1) };
        var after = before with { Id = "after", At = boundary };
        await store.RecordAsync(before, [], TestContext.Current.CancellationToken);
        await store.RecordAsync(after, [], TestContext.Current.CancellationToken);

        var report = await store.QueryAsync(new GainQuery(HistoryLimit: 0), TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Summary.Requests);
        Assert.Equal(0, report.Summary.EstimatedOriginalTokens);
        Assert.Equal(2, report.Summary.EstimatedForwardedTokens);
        Assert.Equal(-2, report.Summary.EstimatedSavedTokens);
        Assert.Equal([boundary.AddDays(-1), boundary], report.Daily.Select(static row => row.Start));
        Assert.Equal([boundary.AddDays(-7), boundary], report.Weekly.Select(static row => row.Start));
        Assert.Equal([DateTimeOffset.Parse("2026-05-01T00:00Z"), boundary], report.Monthly.Select(static row => row.Start));
        Assert.All(report.Daily.Concat(report.Weekly).Concat(report.Monthly), static row =>
        {
            Assert.Equal(1, row.Totals.Requests);
            Assert.Equal(-1, row.Totals.EstimatedSavedTokens);
        });
        Assert.Empty(report.History);
    }

    [Fact]
    public async Task An_empty_or_recall_only_report_keeps_unknown_attribution_and_aggregates_with_no_details()
    {
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-02T12:00Z"));
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path, "gain.db")), clock);

        var empty = await store.QueryAsync(new GainQuery(HistoryLimit: 0), TestContext.Current.CancellationToken);
        await store.RecallAsync(new GainRecall(clock.GetUtcNow(), "preview", null, "unknown", "whole", 289, 100, null),
            null, "retrieve_original", "unknown", "retrieval", "ccr", TestContext.Current.CancellationToken);
        var report = await store.QueryAsync(new GainQuery(HistoryLimit: 0), TestContext.Current.CancellationToken);

        Assert.Equal(new GainTotals(), empty.Summary);
        Assert.Empty(empty.Attributions);
        Assert.Equal(new GainTotals(Recalls: 1, RetrievedCharacters: 289, EstimatedRetrievedTokens: 100), report.Summary);
        Assert.Null(report.Filters.Project);
        Assert.Equal(4, report.Attributions.Count);
        Assert.All(report.Attributions, static row => Assert.Equal(1, row.Totals.Recalls));
        Assert.Empty(report.Daily);
        Assert.Empty(report.Weekly);
        Assert.Empty(report.Monthly);
        Assert.Empty(report.History);
        Assert.Empty(report.Failures);
        Assert.Empty(report.Recalls);
    }

    [Fact]
    public async Task Tied_request_ids_use_ordinal_order_in_the_bounded_history()
    {
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-02T12:00Z"));
        var options = new GainStoreOptions(Path.Combine(directory.Path, "gain.db"));
        var store = new SqliteGainStore(options, clock);
        await store.RecordAsync(Entry("\uE000", "2026-11-01T10:00Z", "/a", "log", 289, 100), [], TestContext.Current.CancellationToken);
        await store.RecordAsync(Entry("\U00010000", "2026-11-01T10:00Z", "/a", "log", 289, 100), [], TestContext.Current.CancellationToken);
        store = new SqliteGainStore(options, clock);

        var report = await store.QueryAsync(new GainQuery(HistoryLimit: 1), TestContext.Current.CancellationToken);

        Assert.Equal("\U00010000", Assert.Single(report.History).Id);
        Assert.Equal(2, report.Summary.Requests);
    }

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
        await store.RecordAsync(first, [new GainFailure(first.At, "a", "/a", "compression", "ExampleFailure")], TestContext.Current.CancellationToken);
        await store.RecordAsync(second, [], TestContext.Current.CancellationToken);
        await store.RecordAsync(third, [], TestContext.Current.CancellationToken);
        await store.RecallAsync(new GainRecall(first.At, "preview", "/a", "caller", "whole", 289, 100, null), "anthropic", "POST", "json,log", "tool_result", "content", TestContext.Current.CancellationToken);
        store = new SqliteGainStore(options, clock);
        var query = new GainQuery("/a", To: DateTimeOffset.Parse("2026-11-02T00:00Z"), Rule: "json,log", HistoryLimit: 1);
        var report = await store.QueryAsync(query, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Summary.Requests);
        Assert.Equal(-100, report.Summary.EstimatedSavedTokens);
        Assert.Equal(1, report.Summary.Recalls);
        Assert.Equal("ExampleFailure", Assert.Single(report.Failures).Error);
        Assert.Equal(DateTimeOffset.Parse("2026-10-31T00:00:00Z"), Assert.Single(report.Daily).Start);
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T00:00:00Z"), Assert.Single(report.Weekly).Start);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00Z"), Assert.Single(report.Monthly).Start);
        Assert.Equal("a", Assert.Single(report.History).Id);
        foreach (var dimension in report.Attributions.GroupBy(static row => row.Dimension)) Assert.Equal(report.Summary.EstimatedSavedTokens, dimension.Sum(static row => row.Totals.EstimatedSavedTokens));
        Assert.Equal("json,log", Assert.Single(report.Attributions, static row => row.Dimension == "rule").Name);
        var injected = await store.QueryAsync(new GainQuery("' OR 1=1 --"), TestContext.Current.CancellationToken);
        Assert.Equal(0, injected.Summary.Requests);
        Assert.Equal(2.89m, report.Measurement.CharactersPerToken);
        Assert.False(report.Measurement.NetSavingsAvailable);
    }

    [Fact]
    public async Task Filtered_reset_preserves_other_projects_newer_events_and_original_database()
    {
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-01T12:00Z"));
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path, "gain.db")), clock);
        await store.RecordAsync(Entry("old", "2026-11-01T10:00Z", "/a", "log", 289, 100), [], TestContext.Current.CancellationToken);
        await store.RecordAsync(Entry("new", "2026-11-01T13:00Z", "/a", "log", 289, 100), [], TestContext.Current.CancellationToken);
        await store.RecordAsync(Entry("other", "2026-11-01T10:00Z", "/b", "log", 289, 100), [], TestContext.Current.CancellationToken);
        var result = await store.ResetAsync(new GainQuery("/a"), TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Removed);
        Assert.Equal(clock.GetUtcNow(), result.Cutoff);
        var report = await store.QueryAsync(new GainQuery(), TestContext.Current.CancellationToken);
        Assert.Equal(2, report.Summary.Requests);
        Assert.DoesNotContain(report.History, row => row.Id == "old");
    }

    [Fact]
    public async Task A_request_path_write_gives_up_quickly_when_another_process_holds_the_database()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "gain.db");
        var store = new SqliteGainStore(new GainStoreOptions(path), TimeProvider.System);
        await store.RecordAsync(Entry("first", "2026-11-01T10:00Z", "/a", "log", 289, 100), [], TestContext.Current.CancellationToken);
        await using var holder = new SqliteConnection($"Data Source={path}");
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using var exclusive = holder.CreateCommand();
        exclusive.CommandText = "BEGIN EXCLUSIVE";
        await exclusive.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await store.RecordAsync(Entry("blocked", "2026-11-01T11:00Z", "/a", "log", 289, 100), [], TestContext.Current.CancellationToken));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed}");
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("method")]
    [InlineData("layer")]
    [InlineData("pipeline")]
    public async Task Each_filter_narrows_the_report_and_the_reset_to_its_own_rows(string dimension)
    {
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-02T12:00Z"));
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path, "gain.db")), clock);
        var kept = Entry("kept", "2026-11-01T10:00Z", "/a", "log", 289, 100);
        var other = dimension switch
        {
            "provider" => kept with { Id = "other", Provider = "openai" },
            "method" => kept with { Id = "other", Method = "GET" },
            "layer" => kept with { Id = "other", Layer = "message" },
            _ => kept with { Id = "other", Pipeline = "passthrough" }
        };
        await store.RecordAsync(kept, [], TestContext.Current.CancellationToken);
        await store.RecordAsync(other, [], TestContext.Current.CancellationToken);
        var query = dimension switch
        {
            "provider" => new GainQuery(Provider: "anthropic"),
            "method" => new GainQuery(Method: "POST"),
            "layer" => new GainQuery(Layer: "tool_result"),
            _ => new GainQuery(Pipeline: "content")
        };

        var report = await store.QueryAsync(query, TestContext.Current.CancellationToken);
        var reset = await store.ResetAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal("kept", Assert.Single(report.History).Id);
        Assert.Equal(1, reset.Removed);
        Assert.Equal("other", Assert.Single((await store.QueryAsync(new GainQuery(), TestContext.Current.CancellationToken)).History).Id);
    }

    [Fact]
    public async Task From_is_inclusive_to_is_exclusive_and_a_sunday_belongs_to_the_week_that_began_on_monday()
    {
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-10T12:00Z"));
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path, "gain.db")), clock);
        await store.RecordAsync(Entry("before", "2026-11-01T23:59:59Z", "/a", "log", 289, 100), [], TestContext.Current.CancellationToken);
        await store.RecordAsync(Entry("sunday", "2026-11-08T00:00Z", "/a", "log", 289, 100), [], TestContext.Current.CancellationToken);
        await store.RecordAsync(Entry("monday", "2026-11-09T00:00Z", "/a", "log", 289, 100), [], TestContext.Current.CancellationToken);

        var report = await store.QueryAsync(
            new GainQuery(From: DateTimeOffset.Parse("2026-11-08T00:00Z"), To: DateTimeOffset.Parse("2026-11-09T00:00Z")),
            TestContext.Current.CancellationToken);
        var all = await store.QueryAsync(new GainQuery(), TestContext.Current.CancellationToken);

        Assert.Equal("sunday", Assert.Single(report.History).Id);
        Assert.Contains(all.Weekly, bucket => bucket.Start == DateTimeOffset.Parse("2026-11-02T00:00Z") && bucket.Totals.Requests == 1);
        Assert.Contains(all.Weekly, bucket => bucket.Start == DateTimeOffset.Parse("2026-11-09T00:00Z") && bucket.Totals.Requests == 1);
    }

    [Fact]
    public async Task A_recall_counts_once_in_each_attribution_dimension_so_the_totals_reconcile_with_the_summary()
    {
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-02T12:00Z"));
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path, "gain.db")), clock);
        await store.RecordAsync(Entry("request", "2026-11-01T10:00Z", "/a", "log", 289, 100), [], TestContext.Current.CancellationToken);
        await store.RecallAsync(
            new GainRecall(DateTimeOffset.Parse("2026-11-01T11:00Z"), "0123456789abcdef", "/a", GainLabels.Attributions.Caller, "whole", 289, 100, null),
            "anthropic", GainLabels.RetrieveOriginal, GainLabels.Unknown, GainLabels.Layers.Retrieval, GainLabels.Pipelines.Ccr, TestContext.Current.CancellationToken);

        var report = await store.QueryAsync(new GainQuery(), TestContext.Current.CancellationToken);

        foreach (var dimension in new[] { "method", "rule", "layer", "pipeline" })
        {
            var attributions = report.Attributions.Where(row => row.Dimension == dimension).ToArray();
            Assert.Equal(report.Summary.Requests, attributions.Sum(static row => row.Totals.Requests));
            Assert.Equal(report.Summary.Recalls, attributions.Sum(static row => row.Totals.Recalls));
            Assert.Equal(report.Summary.EstimatedRetrievedTokens, attributions.Sum(static row => row.Totals.EstimatedRetrievedTokens));
        }

        var ccr = Assert.Single(report.Attributions, static row => row is { Dimension: "pipeline", Name: GainLabels.Pipelines.Ccr });
        Assert.Equal(1, ccr.Totals.Recalls);
        Assert.Equal(0, ccr.Totals.Requests);
    }

    private static GainHistoryEntry Entry(string id, string at, string project, string rule, long original, long forwarded)
    {
        return new GainHistoryEntry(id, DateTimeOffset.Parse(at), project, "anthropic", "POST", rule, "tool_result", "content",
            GainReportBuilder.RequestTotals(original, forwarded, original, forwarded));
    }
}