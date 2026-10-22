using AiProxy.Core.Content;
using AiProxy.Storage;
using Microsoft.Data.Sqlite;

namespace AiProxy.Hosting;

/// <summary>Writes the records the proxy compressed, one queue row at a time.</summary>
public sealed partial class ContentStoreWorker : BackgroundService
{
    private const int BatchSize = 64;

    private readonly IContentWriter _writer;
    private readonly ContentQueue _queue;
    private readonly ContentStoreOptions _options;
    private readonly ILogger<ContentStoreWorker> _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private SqliteConnection? _connection;
    private long _lastDataVersion = -1;

    /// <summary>Holds the collaborators the worker is composed of.</summary>
    /// <param name="writer">Writes a record into the content tables.</param>
    /// <param name="queue">The queue the records wait in.</param>
    /// <param name="options">Where the store keeps what the proxy compressed.</param>
    /// <param name="logger">Where the worker says what it did.</param>
    /// <param name="time">The clock the worker waits on.</param>
    public ContentStoreWorker(
        IContentWriter writer,
        ContentQueue queue,
        ContentStoreOptions options,
        ILogger<ContentStoreWorker> logger,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);

        _writer = writer;
        _queue = queue;
        _options = options;
        _logger = logger;
        _time = time;
        _interval = TimeSpan.FromMilliseconds(250);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log.PassFailed(_logger, exception);
            }

            try
            {
                await Task.Delay(_interval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Drains the queue once, and says how many records it wrote.</summary>
    /// <param name="cancellationToken">The token that stops the pass.</param>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var connection = _connection ??= ContentSchema.Open(_options.ResolvedDatabasePath);
        var version = await ContentQueue.ReadDataVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (version == _lastDataVersion)
        {
            return 0;
        }

        _lastDataVersion = version;

        var jobs = await _queue.ReadAsync(BatchSize, cancellationToken).ConfigureAwait(false);
        var written = 0;

        foreach (var job in jobs)
        {
            try
            {
                await _writer.PersistAsync(job.Record, cancellationToken).ConfigureAwait(false);
                await _queue.RemoveAsync(job.JobId, cancellationToken).ConfigureAwait(false);
                written++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The job stays in the queue, so the next pass has to look again:
                // nothing else committed, and the pragma would say there is nothing new.
                _lastDataVersion = -1;
                Log.JobFailed(_logger, job.JobId, exception);
            }
        }

        if (written > 0)
        {
            Log.Drained(_logger, written);
        }

        return written;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _connection?.Dispose();
        base.Dispose();
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1201, Level = LogLevel.Information,
            Message = "wrote {Count} compressed requests to the store")]
        public static partial void Drained(ILogger logger, int count);

        [LoggerMessage(EventId = 1202, Level = LogLevel.Warning,
            Message = "the queue job {JobId} could not be written and stays queued")]
        public static partial void JobFailed(ILogger logger, long jobId, Exception exception);

        [LoggerMessage(EventId = 1203, Level = LogLevel.Warning,
            Message = "the store pass failed and will be tried again")]
        public static partial void PassFailed(ILogger logger, Exception exception);
    }
}
