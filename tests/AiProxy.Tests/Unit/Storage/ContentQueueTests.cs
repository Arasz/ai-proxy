using AiProxy.Core.Content;
using AiProxy.Storage;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ContentStoreWorker = AiProxy.Hosting.Integration.ContentStoreWorker;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>What the queue does with a row it cannot read, and how a pending record is found.</summary>
public sealed class ContentQueueTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ai-proxy-queue-{Guid.CreateVersion7():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly ContentStoreOptions _options;
    private readonly ContentQueue _queue;
    private readonly SqliteContentStore _store;

    public ContentQueueTests()
    {
        _options = new ContentStoreOptions(_path);
        _queue = new ContentQueue(_options, _time);
        _store = new SqliteContentStore(_options, _queue);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
        {
            File.Delete(file);
        }
    }

    private static ContentRecord Record(string requestId)
    {
        return new ContentRecord(
            requestId,
            new DateTimeOffset(2026, 10, 25, 11, 0, 0, TimeSpan.Zero),
            "original",
            "short",
            0.5,
            "log.signal-filter",
            1,
            [new CompressedPart(1, ContentType.Log, "short", "original", "log.signal-filter", 0.5)]);
    }

    private SqliteConnection OpenReady()
    {
        var connection = ContentSchema.Open(_path);
        connection.Ensure();
        return connection;
    }

    [Fact]
    public async Task A_row_that_cannot_be_read_is_dead_lettered_and_the_valid_record_behind_it_is_written()
    {
        var token = TestContext.Current.CancellationToken;
        using (var connection = OpenReady())
        {
            connection.Execute("INSERT INTO content_queue (request_id, created_at, payload) VALUES ('bad', 't', '{not json'), ('null', 't', 'null');");
        }

        await _queue.PublishAsync(Record("good"), token);
        var worker = new ContentStoreWorker(_store, _queue, _options, NullLogger<ContentStoreWorker>.Instance, _time);

        var written = await worker.RunOnceAsync(token);

        using var check = OpenReady();
        Assert.Equal(1, written);
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT count(*) FROM content_queue;"));
        var dead = Assert.Single(check.Query<DeadRow>("SELECT request_id AS RequestId, payload AS Payload, error AS Error FROM content_queue_dead;"));
        Assert.Equal("bad", dead.RequestId);
        Assert.Equal("{not json", dead.Payload);
        Assert.False(string.IsNullOrWhiteSpace(dead.Error));
        Assert.NotNull(await _store.GetAsync("good", token));
    }

    [Fact]
    public async Task A_pending_record_beyond_the_first_batch_is_still_served_by_its_request_id()
    {
        var token = TestContext.Current.CancellationToken;
        for (var index = 1; index <= 70; index++)
        {
            await _queue.PublishAsync(Record($"request-{index}"), token);
        }

        var found = await _store.GetAsync("request-65", token);

        Assert.Equal("request-65", found?.RequestId);
    }

    private sealed record DeadRow(string RequestId, string Payload, string Error);
}
