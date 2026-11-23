using AiProxy.Core.Gain;
using AiProxy.Storage;
using AiProxy.Tests.Integration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests.Unit.Gain;

/// <summary>The store gives the last report again while the ledger version stays the same, and runs the report again when it changes.</summary>
public sealed class GainReportCacheTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-02T12:00Z");
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unchanged_ledger_gives_the_cached_report_with_a_new_generated_time()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "gain.db");
        var clock = new FakeTimeProvider(At);
        var store = new SqliteGainStore(new GainStoreOptions(path), clock);
        await store.RecordAsync(Entry("a", 300, 100), [], Cancellation);
        var first = await store.QueryAsync(new GainQuery(), Cancellation);

        // An edit in place keeps MAX(sequence) and COUNT(*), so only a report run again can show it.
        EditInPlace(path, 900);
        clock.Advance(TimeSpan.FromSeconds(2));
        var second = await store.QueryAsync(new GainQuery(), Cancellation);

        Assert.Equal(300, second.Summary.OriginalCharacters);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(At.AddSeconds(2), second.GeneratedAt);
    }

    [Fact]
    public async Task A_recorded_request_makes_the_next_query_run_again()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path, "gain.db")), new FakeTimeProvider(At));
        await store.RecordAsync(Entry("a", 300, 100), [], Cancellation);
        await store.QueryAsync(new GainQuery(), Cancellation);

        await store.RecordAsync(Entry("b", 500, 100), [], Cancellation);
        var report = await store.QueryAsync(new GainQuery(), Cancellation);

        Assert.Equal(2, report.Summary.Requests);
        Assert.Equal(800, report.Summary.OriginalCharacters);
    }

    [Fact]
    public async Task A_reset_that_removes_older_rows_makes_the_next_query_run_again()
    {
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(At);
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path, "gain.db")), clock);
        await store.RecordAsync(Entry("old", 300, 100, "/old"), [], Cancellation);
        await store.RecordAsync(Entry("new", 500, 100, "/new"), [], Cancellation);
        await store.QueryAsync(new GainQuery(), Cancellation);

        // The newest row stays, so MAX(sequence) does not move; only the count shows the reset.
        await store.ResetAsync(new GainQuery("/old"), "test", Cancellation);
        var report = await store.QueryAsync(new GainQuery(), Cancellation);

        Assert.Equal(1, report.Summary.Requests);
        Assert.Equal(500, report.Summary.OriginalCharacters);
    }

    [Fact]
    public async Task A_write_from_another_store_on_the_same_file_makes_the_next_query_run_again()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "gain.db");
        var store = new SqliteGainStore(new GainStoreOptions(path), new FakeTimeProvider(At));
        var other = new SqliteGainStore(new GainStoreOptions(path), new FakeTimeProvider(At));
        await store.RecordAsync(Entry("a", 300, 100), [], Cancellation);
        await store.QueryAsync(new GainQuery(), Cancellation);

        await other.RecordAsync(Entry("b", 500, 100), [], Cancellation);
        var report = await store.QueryAsync(new GainQuery(), Cancellation);

        Assert.Equal(2, report.Summary.Requests);
    }

    [Fact]
    public async Task A_different_filter_does_not_get_the_cached_report()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path, "gain.db")), new FakeTimeProvider(At));
        await store.RecordAsync(Entry("a", 300, 100, "/a"), [], Cancellation);
        await store.RecordAsync(Entry("b", 500, 100, "/b"), [], Cancellation);
        await store.QueryAsync(new GainQuery(), Cancellation);

        var report = await store.QueryAsync(new GainQuery("/b"), Cancellation);

        Assert.Equal(1, report.Summary.Requests);
        Assert.Equal(500, report.Summary.OriginalCharacters);
        Assert.Equal("/b", report.Filters.Project);
    }

    [Fact]
    public async Task A_schema_version_change_makes_the_next_query_run_again()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "gain.db");
        var store = new SqliteGainStore(new GainStoreOptions(path), new FakeTimeProvider(At));
        await store.RecordAsync(Entry("a", 300, 100), [], Cancellation);
        await store.QueryAsync(new GainQuery(), Cancellation);

        // A newer build's schema step rewrites rows in place and raises user_version.
        EditInPlace(path, 900, "PRAGMA user_version = 99;");
        var report = await store.QueryAsync(new GainQuery(), Cancellation);

        Assert.Equal(900, report.Summary.OriginalCharacters);
    }

    [Fact]
    public async Task A_cached_report_carries_the_filters_it_was_asked_for()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteGainStore(new GainStoreOptions(Path.Combine(directory.Path, "gain.db")), new FakeTimeProvider(At));
        await store.RecordAsync(Entry("a", 300, 100), [], Cancellation);
        await store.QueryAsync(new GainQuery(From: DateTimeOffset.Parse("2026-10-31T23:00Z")), Cancellation);

        var asked = DateTimeOffset.Parse("2026-11-01T00:00+01:00");
        var report = await store.QueryAsync(new GainQuery(From: asked), Cancellation);

        Assert.Equal(asked.Offset, report.Filters.From!.Value.Offset);
    }

    private static void EditInPlace(string path, long originalCharacters, string also = "")
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = also + "UPDATE gain_event SET payload = json_set(payload,'$.Totals.OriginalCharacters',$value) WHERE kind = 'request'";
        command.Parameters.AddWithValue("$value", originalCharacters);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static GainHistoryEntry Entry(string id, long original, long forwarded, string project = "/a") =>
        new(id, At, project, "anthropic", "POST", "log", "tool_result", "content",
            GainReportBuilder.RequestTotals(original, forwarded, original, forwarded));
}
