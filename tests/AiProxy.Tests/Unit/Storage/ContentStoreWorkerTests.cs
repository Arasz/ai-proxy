using AiProxy.Core.Content;
using AiProxy.Hosting;
using AiProxy.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ContentStoreWorker = AiProxy.Hosting.Integration.ContentStoreWorker;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>The worker that drains the queue, tested through its pass.</summary>
public sealed class ContentStoreWorkerTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"ai-proxy-worker-{Guid.CreateVersion7():N}.db");

    private readonly ContentStoreOptions _options;
    private readonly ContentQueue _queue;
    private readonly SqliteContentStore _store;
    private readonly FakeTimeProvider _time = new();

    public ContentStoreWorkerTests()
    {
        _options = new ContentStoreOptions(_databasePath);
        _queue = new ContentQueue(_options);
        _store = new SqliteContentStore(_options, _queue);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var file in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
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

    /// <summary>Makes the tables, as the host does when the store starts.</summary>
    private void EnsureSchema()
    {
        using var connection = ContentSchema.Open(_databasePath);
        connection.Ensure();
    }

    private ContentStoreWorker Worker(IContentWriter writer)
    {
        return new ContentStoreWorker(writer, _queue, _options, NullLogger<ContentStoreWorker>.Instance, _time);
    }

    [Fact]
    public async Task A_pass_writes_the_record_and_takes_it_out_of_the_queue()
    {
        await _store.StoreAsync(Record(), CancellationToken.None);
        var worker = Worker(_store);

        var written = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, written);
        Assert.Empty(await _queue.ReadAsync(10, CancellationToken.None));
        var stored = await _store.GetAsync("request-1", CancellationToken.None);
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task A_job_that_fails_stays_in_the_queue_and_is_tried_again()
    {
        EnsureSchema();
        await _store.StoreAsync(Record(), CancellationToken.None);
        var writer = new RecordingWriter { Failures = 1 };
        var worker = Worker(writer);

        var first = await worker.RunOnceAsync(CancellationToken.None);
        var second = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, first);
        Assert.Equal(1, second);
        Assert.Equal(2, writer.Calls);
    }

    [Fact]
    public async Task A_second_pass_with_nothing_new_reads_nothing()
    {
        // The poll is gated on the pragma that says whether another connection committed,
        // so the second pass with nothing new must not read the queue at all. Counting
        // the writes would not prove it: there is nothing to write either way.
        EnsureSchema();
        var queue = new CountingQueue(_queue);
        var worker = new ContentStoreWorker(
            new RecordingWriter(),
            queue,
            _options,
            NullLogger<ContentStoreWorker>.Instance,
            _time);

        await worker.RunOnceAsync(CancellationToken.None);
        var readsAfterTheFirstPass = queue.Reads;
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, readsAfterTheFirstPass);
        Assert.Equal(1, queue.Reads);
    }

    [Fact]
    public async Task A_pass_after_the_producer_committed_reads_again()
    {
        EnsureSchema();
        var queue = new CountingQueue(_queue);
        var worker = new ContentStoreWorker(
            new RecordingWriter(),
            queue,
            _options,
            NullLogger<ContentStoreWorker>.Instance,
            _time);

        await worker.RunOnceAsync(CancellationToken.None);
        await _store.StoreAsync(Record(), CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, queue.Reads);
    }

    private sealed class CountingQueue(IContentQueue inner) : IContentQueue
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyList<ContentJob>> ReadAsync(int limit, CancellationToken cancellationToken)
        {
            Reads++;
            return inner.ReadAsync(limit, cancellationToken);
        }

        public ValueTask RemoveAsync(long jobId, CancellationToken cancellationToken)
        {
            return inner.RemoveAsync(jobId, cancellationToken);
        }

        public Task<long> ReadDataVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            return inner.ReadDataVersionAsync(connection, cancellationToken);
        }
    }

    private sealed class RecordingWriter : IContentWriter
    {
        public int Calls { get; private set; }

        public int Failures { get; init; }

        public ValueTask PersistAsync(ContentRecord record, CancellationToken cancellationToken)
        {
            Calls++;

            if (Calls <= Failures)
            {
                throw new InvalidOperationException("the disk is full");
            }

            return ValueTask.CompletedTask;
        }
    }
}