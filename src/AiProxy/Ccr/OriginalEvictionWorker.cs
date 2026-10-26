using AiProxy.Storage;

namespace AiProxy.Ccr;

/// <summary>Deletes CCR originals and ledger rows past their retention, once at start and then every hour.</summary>
public sealed partial class OriginalEvictionWorker(SqliteOriginalStore store, ILogger<OriginalEvictionWorker> logger, TimeProvider time) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
            try
            {
                var evicted = await store.EvictAsync(stoppingToken).ConfigureAwait(false);
                if (evicted > 0) Log.Evicted(logger, evicted);

                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log.EvictionFailed(logger, exception);
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1611, Level = LogLevel.Information, Message = "evicted {Count} CCR originals and ledger rows past their retention")]
        public static partial void Evicted(ILogger logger, int count);

        [LoggerMessage(EventId = 1612, Level = LogLevel.Warning, Message = "CCR eviction failed and will be tried again in an hour")]
        public static partial void EvictionFailed(ILogger logger, Exception exception);
    }
}