using AiProxy.Core.Content;
using AiProxy.Storage;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>
/// The store, tested against a real database file: a fake would pass where the SQL is wrong.
/// </summary>
public class ContentStoreTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"ai-proxy-store-{Guid.NewGuid():N}.db");
    private readonly ContentStoreOptions _options;
    private readonly ContentQueue _queue;
    private readonly SqliteContentStore _store;

    public ContentStoreTests()
    {
        _options = new ContentStoreOptions(_databasePath);
        _queue = new ContentQueue(_options);
        _store = new SqliteContentStore(_options, _queue);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var file in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static ContentRecord Record() => new(
        "request-1",
        new DateTimeOffset(2026, 3, 12, 10, 0, 0, TimeSpan.Zero),
        "the original bödy",
        "shört",
        6.0 / 18.0,
        "log.signal-filter",
        1,
        [new CompressedPart(1, ContentType.Log, "the compressed part", "the original part", "log.signal-filter", 0.5)]);

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

        Assert.Equal("wal", mode, ignoreCase: true);
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
                new CompressedPart(1, ContentType.Log, "the compressed part", null!, "log.signal-filter", 0.5),
            ],
        };

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await _store.PersistAsync(record, CancellationToken.None));

        await using var connection = ContentSchema.Open(_databasePath);
        var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM content_request;");

        Assert.Equal(0, count);
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
            (double)System.Text.Encoding.UTF8.GetByteCount(row.Compressed) /
            System.Text.Encoding.UTF8.GetByteCount(row.Original),
            row.Ratio,
            6);
    }
}
