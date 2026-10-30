using AiProxy.Storage;

namespace AiProxy.Hosting.Integration;

/// <summary>Deletes captured requests past their retention, once at start and then every hour.</summary>
public sealed partial class CaptureEvictionWorker(SqliteCaptureStore store, ILogger<CaptureEvictionWorker> logger, TimeProvider time) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var evicted = await store.EvictAsync(stoppingToken).ConfigureAwait(false);
                if (evicted > 0)
                {
                    Log.Evicted(logger, evicted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log.EvictionFailed(logger, exception);
            }

            try
            {
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1621, Level = LogLevel.Information, Message = "evicted {Count} captured rows past their retention")]
        public static partial void Evicted(ILogger logger, int count);

        [LoggerMessage(EventId = 1622, Level = LogLevel.Warning, Message = "capture eviction failed and will be tried again in an hour")]
        public static partial void EvictionFailed(ILogger logger, Exception exception);
    }
}
