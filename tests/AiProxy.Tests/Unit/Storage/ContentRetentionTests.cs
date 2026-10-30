using System.Globalization;
using AiProxy.Storage;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>What the retention pass keeps and removes in the content file, table by table.</summary>
public sealed class ContentRetentionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ai-proxy-retention-{Guid.CreateVersion7():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteOriginalStore _store;

    public ContentRetentionTests()
    {
        _store = new SqliteOriginalStore(new ContentStoreOptions(_path), _time, NullLogger<SqliteOriginalStore>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
        {
            File.Delete(file);
        }
    }

    private string DaysAgo(int days)
    {
        return (_time.GetUtcNow() - TimeSpan.FromDays(days)).ToString("O", CultureInfo.InvariantCulture);
    }

    private SqliteConnection OpenReady()
    {
        var connection = ContentSchema.Open(_path);
        connection.Ensure();
        return connection;
    }

    [Fact]
    public async Task A_pass_removes_requests_older_than_fourteen_days_with_their_chunks_and_keeps_the_newer_ones()
    {
        using (var connection = OpenReady())
        {
            foreach (var (id, days) in new[] { ("new", 13), ("old", 15) })
            {
                connection.Execute(
                    "INSERT INTO content_request VALUES (@id, @at, 'o', 'c', 1, 'p', 1); INSERT INTO content_chunk VALUES (@id, 1, 'Log', 'o', 'c', 'p', 1);",
                    new { id, at = DaysAgo(days) });
            }
        }

        await _store.EvictAsync(TestContext.Current.CancellationToken);

        using var check = OpenReady();
        Assert.Equal(["new"], check.Query<string>("SELECT request_id FROM content_request;").ToList());
        Assert.Equal(["new"], check.Query<string>("SELECT request_id FROM content_chunk;").ToList());
    }

    [Fact]
    public async Task A_pass_removes_dead_letters_originals_and_ledger_rows_older_than_fourteen_days()
    {
        using (var connection = OpenReady())
        {
            foreach (var (name, days) in new[] { ("new", 13), ("old", 15) })
            {
                connection.Execute(
                    """
                    INSERT INTO content_queue_dead (job_id, request_id, created_at, failed_at, payload, error) VALUES (@Job, @name, @at, @at, 'x', 'e');
                    INSERT INTO ccr_original VALUES (@name, 't', 1, @at);
                    INSERT INTO ccr_thread VALUES (@name, 'c', @at);
                    """,
                    new { name, at = DaysAgo(days), Job = days });
            }
        }

        await _store.EvictAsync(TestContext.Current.CancellationToken);

        using var check = OpenReady();
        Assert.Equal(["new"], check.Query<string>("SELECT request_id FROM content_queue_dead;").ToList());
        Assert.Equal(["new"], check.Query<string>("SELECT id FROM ccr_original;").ToList());
        Assert.Equal(["new"], check.Query<string>("SELECT message_id FROM ccr_thread;").ToList());
    }

    [Fact]
    public async Task A_pass_removes_more_old_rows_than_one_batch_holds()
    {
        const int Count = 2_500;
        using (var connection = OpenReady())
        {
            using var transaction = connection.BeginTransaction();
            for (var index = 0; index < Count; index++)
            {
                connection.Execute(
                    "INSERT INTO content_request VALUES (@id, @at, 'o', 'c', 1, 'p', 0);",
                    new { id = $"r{index}", at = DaysAgo(20) },
                    transaction);
            }

            transaction.Commit();
        }

        var removed = await _store.EvictAsync(TestContext.Current.CancellationToken);

        using var check = OpenReady();
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT count(*) FROM content_request;"));
        Assert.Equal(Count, removed);
    }
}
