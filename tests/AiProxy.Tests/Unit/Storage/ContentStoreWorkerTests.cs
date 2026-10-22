using AiProxy.Core.Content;
using AiProxy.Hosting;
using AiProxy.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests.Unit.Storage;

/// <summary>The worker that drains the queue, tested through its pass.</summary>
public class ContentStoreWorkerTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"ai-proxy-worker-{Guid.NewGuid():N}.db");
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

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
        "the original body",
        "short",
        5.0 / 17.0,
        "log.signal-filter",
        1,
        [new CompressedPart(1, ContentType.Log, "the compressed part", "the original part", "log.signal-filter", 0.5)]);

    private ContentStoreWorker Worker(IContentWriter writer) =>
        new(writer, _queue, _options, NullLogger<ContentStoreWorker>.Instance, _time);

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
        var writer = new RecordingWriter();
        var worker = Worker(writer);

        await worker.RunOnceAsync(CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, writer.Calls);
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
