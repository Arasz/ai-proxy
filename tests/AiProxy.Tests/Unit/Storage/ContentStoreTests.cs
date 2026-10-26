using System.Text;
using AiProxy.Core.Content;
using AiProxy.Storage;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>
///     The store, tested against a real database file: a fake would pass where the SQL is wrong.
/// </summary>
public sealed class ContentStoreTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"ai-proxy-store-{Guid.CreateVersion7():N}.db");

    private readonly ContentQueue _queue;
    private readonly SqliteContentStore _store;

    public ContentStoreTests()
    {
        var options = new ContentStoreOptions(_databasePath);
        _queue = new ContentQueue(options);
        _store = new SqliteContentStore(options, _queue);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var file in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
            if (File.Exists(file))
                File.Delete(file);
    }

    private static ContentRecord Record()
    {
        return new ContentRecord(
            "request-1",
            new DateTimeOffset(2026, 3, 12, 10, 0, 0, TimeSpan.Zero),
            "the original bödy",
            "shört",
            6.0 / 18.0,
            "log.signal-filter",
            1,
            [new CompressedPart(1, ContentType.Log, "the compressed part", "the original part", "log.signal-filter", 0.5)]);
    }

    [Fact]
    public async Task A_published_record_is_in_the_queue_when_the_producer_returns()
    {
        await _store.StoreAsync(Record(), CancellationToken.None);

        var jobs = await _queue.ReadAsync(10, CancellationToken.None);

        Assert.Single(jobs);
    }

    [Fact]
    public async Task A_record_that_the_writer_wrote_comes_back_with_its_fields()
    {
        var record = Record();
        await _store.StoreAsync(record, CancellationToken.None);
        await _store.PersistAsync(record, CancellationToken.None);

        var stored = await _store.GetAsync("request-1", CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal("the original bödy", stored.Original);
        Assert.Equal("shört", stored.Compressed);
        Assert.Equal(6.0 / 18.0, stored.Ratio, 6);
        Assert.Equal("log.signal-filter", stored.Path);
        Assert.Equal(1, stored.ChunkCount);
        Assert.Equal("the compressed part", Assert.Single(stored.Parts).Text);
    }

    [Fact]
    public async Task A_record_that_is_still_queued_is_served_too()
    {
        await _store.StoreAsync(Record(), CancellationToken.None);

        var stored = await _store.GetAsync("request-1", CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal("the original bödy", stored.Original);
    }

    [Fact]
    public async Task An_unknown_request_id_is_null()
    {
        var stored = await _store.GetAsync("never-stored", CancellationToken.None);

        Assert.Null(stored);
    }

    [Fact]
    public async Task The_database_runs_in_wal_mode()
    {
        await _store.StoreAsync(Record(), CancellationToken.None);

        await using var connection = ContentSchema.Open(_databasePath);
        var mode = await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;");

        Assert.Equal("wal", mode, true);
    }

    [Fact]
    public async Task A_reader_on_another_connection_sees_the_committed_row()
    {
        await _store.StoreAsync(Record(), CancellationToken.None);

        await using var connection = ContentSchema.Open(_databasePath);
        var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM content_queue;");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task A_record_whose_chunks_cannot_be_written_leaves_no_request_row()
    {
        // The request row and its chunks are one fact. A chunk that cannot be written
        // takes the request row with it, or a reader would find a record that says how
        // many parts there were and shows none of them.
        var record = Record() with
        {
            Parts =
            [
                new CompressedPart(1, ContentType.Log, "the compressed part", null!, "log.signal-filter", 0.5)
            ]
        };

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await _store.PersistAsync(record, CancellationToken.None));

        await using var connection = ContentSchema.Open(_databasePath);
        var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM content_request;");

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task A_reader_is_never_locked_out_by_a_writer()
    {
        var record = Record();
        await _store.PersistAsync(record, TestContext.Current.CancellationToken);

        await using var writer = ContentSchema.Open(_databasePath);
        writer.Execute("BEGIN IMMEDIATE;");
        try
        {
            await writer.ExecuteAsync(
                "UPDATE content_request SET original = @Original WHERE request_id = @RequestId;",
                new { Original = "uncommitted change", record.RequestId });

            var whileWriting = await _store.GetAsync(record.RequestId, TestContext.Current.CancellationToken);

            Assert.NotNull(whileWriting);
            Assert.Equal(record.Original, whileWriting.Original);
            Assert.Equal("the compressed part", Assert.Single(whileWriting.Parts).Text);
        }
        finally
        {
            writer.Execute("ROLLBACK;");
        }

        var afterWriting = await _store.GetAsync(record.RequestId, TestContext.Current.CancellationToken);
        Assert.NotNull(afterWriting);
        Assert.Equal(record.Original, afterWriting.Original);
    }

    [Fact]
    public async Task A_writer_that_loses_the_lock_says_so_and_nothing_else()
    {
        // Two writers that each hold a read snapshot and then want to write are a
        // deadlock, and SQLite breaks it by refusing one of them straight away rather
        // than by waiting. The worker is built for that: the job stays in the queue and
        // the next pass writes it. What the store must not do is fail in some other way.
        var work = new List<Task>();

        for (var index = 0; index < 10; index++)
        {
            var requestId = $"request-{index}";

            work.Add(Task.Run(async () => await _store.PersistAsync(
                    Record() with { RequestId = requestId },
                    TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken));
        }

        var failures = new List<Exception>();

        foreach (var write in work)
            try
            {
                await write;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

        Assert.All(failures, failure =>
        {
            var locked = Assert.IsType<SqliteException>(failure);
            Assert.Contains("locked", locked.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact(Skip = TimingDependent.Reason)]
    public async Task The_producer_gives_up_quickly_when_another_connection_holds_the_lock()
    {
        // The producer's write stands between a request and its provider, so it waits a
        // second and not the half minute a command waits by default. A request must not
        // hang on a locked database: the record is lost, the request goes out, and the
        // pipeline logs that the store failed.
        // The schema exists before the lock is taken, as it does in the host: a schema
        // change cannot wait for the write lock, so creating the tables under a held
        // lock is a different thing from writing a row under one.
        using (var setup = ContentSchema.Open(_databasePath))
        {
            setup.Ensure();
        }

        await using var holder = ContentSchema.Open(_databasePath);
        holder.Execute("BEGIN IMMEDIATE;");

        var started = DateTimeOffset.UtcNow;

        await Assert.ThrowsAnyAsync<SqliteException>(async () =>
            await _store.StoreAsync(Record(), TestContext.Current.CancellationToken));

        var waited = DateTimeOffset.UtcNow - started;
        Assert.True(
            waited < TimeSpan.FromSeconds(10),
            $"the producer waited {waited.TotalSeconds:F1} seconds for the lock");

        holder.Execute("COMMIT;");
    }

    [Fact]
    public async Task The_stored_ratio_agrees_with_the_stored_texts()
    {
        var record = Record();
        await _store.StoreAsync(record, CancellationToken.None);
        await _store.PersistAsync(record, CancellationToken.None);

        await using var connection = ContentSchema.Open(_databasePath);
        var row = await connection.QuerySingleAsync<(string Original, string Compressed, double Ratio)>(
            "SELECT original AS Original, compressed AS Compressed, ratio AS Ratio FROM content_request WHERE request_id = @RequestId;",
            new { record.RequestId });

        Assert.Equal(
            (double)Encoding.UTF8.GetByteCount(row.Compressed) /
            Encoding.UTF8.GetByteCount(row.Original),
            row.Ratio,
            6);
    }
}