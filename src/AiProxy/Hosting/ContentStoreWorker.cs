using AiProxy.Storage;
using Microsoft.Data.Sqlite;

namespace AiProxy.Hosting;

/// <summary>Writes the records the proxy compressed, one queue row at a time.</summary>
public sealed partial class ContentStoreWorker(IContentWriter writer, IContentQueue queue, ContentStoreOptions options, ILogger<ContentStoreWorker> logger, TimeProvider time) : BackgroundService
{
    private const int BatchSize = 64;

    private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(250);
    private SqliteConnection? _connection;
    private long _lastDataVersion = -1;

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
                Log.PassFailed(logger, exception);
            }

            try
            {
                await Task.Delay(_interval, time, stoppingToken).ConfigureAwait(false);
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
        var connection = _connection ??= ContentSchema.Open(options.ResolvedDatabasePath);
        var version = await queue.ReadDataVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (version == _lastDataVersion)
        {
            return 0;
        }

        _lastDataVersion = version;

        var jobs = await queue.ReadAsync(BatchSize, cancellationToken).ConfigureAwait(false);
        var written = 0;

        foreach (var job in jobs)
        {
            try
            {
                await writer.PersistAsync(job.Record, cancellationToken).ConfigureAwait(false);
                await queue.RemoveAsync(job.JobId, cancellationToken).ConfigureAwait(false);
                written++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _lastDataVersion = -1;
                Log.JobFailed(logger, job.JobId, exception);
            }
        }

        if (written > 0)
        {
            Log.Drained(logger, written);
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
