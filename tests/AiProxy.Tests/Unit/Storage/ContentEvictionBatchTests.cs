using System.Globalization;
using AiProxy.Storage;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>An eviction batch deletes a bounded number of bytes, so the write lock is held for a short time.</summary>
public sealed class ContentEvictionBatchTests : IDisposable
{
    private const int RowBytes = 500_000;
    private const int RowCount = 120;
    private const string Series = "WITH RECURSIVE s(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM s WHERE value < @count) ";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ai-proxy-batch-{Guid.CreateVersion7():N}.db");
    private readonly DateTimeOffset _now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

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
        return (_now - TimeSpan.FromDays(days)).ToString("O", CultureInfo.InvariantCulture);
    }

    private SqliteConnection OpenReady()
    {
        var connection = ContentSchema.Open(_path);
        connection.Ensure();
        return connection;
    }

    /// <summary>Logs the bytes of each deleted row; the observer sums and clears the log after every batch.</summary>
    private static void LogDeletedBytes(SqliteConnection connection, string table, string column)
    {
        connection.Execute($"""
                            CREATE TABLE deleted_bytes (n INTEGER NOT NULL);
                            CREATE TRIGGER log_{table} AFTER DELETE ON {table} BEGIN INSERT INTO deleted_bytes VALUES (length(old.{column})); END;
                            """);
    }

    private async Task<List<long>> EvictRecordingBatchesAsync()
    {
        var batches = new List<long>();
        using var observer = ContentSchema.Open(_path);
        await using var connection = ContentSchema.Open(_path);
        var removed = await connection.EvictOlderThanAsync(
            RetentionRules.Content,
            _now - TimeSpan.FromDays(14),
            TestContext.Current.CancellationToken,
            () =>
            {
                batches.Add(observer.ExecuteScalar<long>("SELECT COALESCE(SUM(n), 0) FROM deleted_bytes;"));
                observer.Execute("DELETE FROM deleted_bytes;");
            });
        Assert.True(removed > 0);
        return batches;
    }

    [Fact]
    public async Task A_batch_of_large_originals_stays_inside_the_byte_budget_and_the_pass_still_removes_every_expired_row()
    {
        using (var setup = OpenReady())
        {
            LogDeletedBytes(setup, "ccr_original", "text");
            setup.Execute(
                Series + "INSERT INTO ccr_original SELECT 'old' || value, replace(hex(zeroblob(@half)), '0', 'a'), 1, @old FROM s;" +
                "INSERT INTO ccr_original VALUES ('young', 'keep', 1, @young);",
                new { half = RowBytes / 2, count = RowCount, old = DaysAgo(20), young = DaysAgo(1) });
        }

        var batches = await EvictRecordingBatchesAsync();

        Assert.All(batches, bytes => Assert.True(bytes <= ContentRetention.BatchBudgetBytes, $"a batch deleted {bytes} bytes"));
        Assert.True(batches.Count > 1);
        using var check = OpenReady();
        Assert.Equal(["young"], check.Query<string>("SELECT id FROM ccr_original;").ToList());
    }

    [Fact]
    public async Task A_batch_counts_the_chunks_of_a_request_in_its_bytes()
    {
        using (var setup = OpenReady())
        {
            LogDeletedBytes(setup, "content_chunk", "original_text");
            setup.Execute(
                Series + "INSERT INTO content_request SELECT 'r' || value, @old, 'o', 'c', 1, 'p', 1 FROM s;" +
                Series + "INSERT INTO content_chunk SELECT 'r' || value, 1, 'Log', replace(hex(zeroblob(@half)), '0', 'a'), 'c', 'p', 1 FROM s;",
                new { half = RowBytes / 2, count = RowCount, old = DaysAgo(20) });
        }

        var batches = await EvictRecordingBatchesAsync();

        Assert.All(batches, bytes => Assert.True(bytes <= ContentRetention.BatchBudgetBytes, $"a batch deleted {bytes} bytes"));
        using var check = OpenReady();
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT (SELECT count(*) FROM content_request) + (SELECT count(*) FROM content_chunk);"));
    }

    [Theory]
    [InlineData("ccr_original")]
    [InlineData("ccr_thread")]
    [InlineData("content_request")]
    public async Task A_row_refreshed_between_the_pick_and_the_delete_survives_with_its_chunks(string table)
    {
        using (var setup = OpenReady())
        {
            setup.Execute(
                "INSERT INTO ccr_original VALUES ('o', 't', 1, @old); INSERT INTO ccr_thread VALUES ('o', 'c', @old);" +
                "INSERT INTO content_request VALUES ('o', @old, 'o', 'c', 1, 'p', 1); INSERT INTO content_chunk VALUES ('o', 1, 'Log', 'o', 'c', 'p', 1);",
                new { old = DaysAgo(20) });
        }

        using var refresher = ContentSchema.Open(_path);
        await using var connection = ContentSchema.Open(_path);
        var rule = RetentionRules.Content.Single(candidate => candidate.Table == table);
        await connection.EvictOlderThanAsync(
            [rule],
            _now - TimeSpan.FromDays(14),
            TestContext.Current.CancellationToken,
            afterPick: () => refresher.Execute($"UPDATE {table} SET {rule.Column} = '{DaysAgo(1)}';"));

        using var check = OpenReady();
        Assert.Equal(1, check.ExecuteScalar<int>($"SELECT count(*) FROM {table};"));
        if (table == "content_request")
        {
            Assert.Equal(1, check.ExecuteScalar<int>("SELECT count(*) FROM content_chunk;"));
        }
    }

    [Theory]
    [InlineData("content_request", "content_request_created")]
    [InlineData("ccr_original", "ccr_original_created")]
    [InlineData("ccr_thread", "ccr_thread_created")]
    [InlineData("content_queue_dead", "content_queue_dead_failed")]
    public void The_candidate_query_of_a_table_reads_its_age_index_and_sorts_nothing(string table, string index)
    {
        var plan = PlanOf(table);

        Assert.Contains(plan, line => line.Contains($"INDEX {index}", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public void The_size_of_a_request_reads_its_chunks_by_primary_key()
    {
        var plan = PlanOf("content_request");

        Assert.Contains(plan, line => line.Contains("content_chunk", StringComparison.Ordinal) && line.Contains("INDEX", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN content_chunk", StringComparison.Ordinal));
    }

    private List<string> PlanOf(string table)
    {
        var rule = RetentionRules.Content.Single(candidate => candidate.Table == table);
        using var connection = OpenReady();
        using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {ContentRetention.CandidateQuery(rule)}";
        command.Parameters.AddWithValue("@Cutoff", DaysAgo(14));
        command.Parameters.AddWithValue("@Batch", 1000);
        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }
}
