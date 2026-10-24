using AiProxy.Core.Capture;
using AiProxy.Core.Timing;
using AiProxy.Core.Compression;
using AiProxy.Core.Providers;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;

namespace AiProxy.Hosting;

/// <summary>Inspects, captures and compresses requests sent to provider API hosts.</summary>
public sealed partial class ProviderRequestInspector
{
    private const long MaxInspectedBodyBytes = 8 * 1024 * 1024;

    private readonly ICompressionPipeline _compression;
    private readonly IProviderHosts _providerHosts;
    private readonly bool _compressionEnabled;
    private readonly ILogger<ProviderRequestInspector> _logger;
    private readonly IRequestCaptureStore? _capture;
    private readonly TimeProvider _time;

    /// <summary>Creates an inspector; with compression disabled and no capture store every request passes through untouched.</summary>
    /// <param name="capture">Where every provider request body is kept as sent; null leaves capture off.</param>
    /// <param name="time">The clock that stamps each capture and times its stages; null uses the system clock.</param>
    public ProviderRequestInspector(
        ICompressionPipeline compression,
        IProviderHosts providerHosts,
        bool compressionEnabled,
        ILogger<ProviderRequestInspector> logger,
        IRequestCaptureStore? capture = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(compression);
        ArgumentNullException.ThrowIfNull(providerHosts);
        ArgumentNullException.ThrowIfNull(logger);
        _compression = compression;
        _providerHosts = providerHosts;
        _compressionEnabled = compressionEnabled;
        _logger = logger;
        _capture = capture;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Whether capture is on, so the proxy has to time exchanges and hand over each finished response.</summary>
    internal bool CapturesResponses => _capture is not null;

    /// <summary>Leaves unknown hosts untouched and processes known provider requests.</summary>
    public async Task BeforeRequestAsync(object sender, SessionEventArgs e)
    {
        if (!_compressionEnabled && _capture is null)
        {
            return;
        }

        var request = e.HttpClient.Request;
        if (_providerHosts.Find(request.RequestUri.Host) is not { } dialect || !request.HasBody)
        {
            return;
        }

        if (request.ContentLength is < 0 or > MaxInspectedBodyBytes)
        {
            return;
        }

        var started = _time.GetTimestamp();
        var exchange = new CapturedExchange { Dialect = dialect };
        var captureId = Guid.NewGuid().ToString("N");

        var stage = _time.GetTimestamp();
        var body = await e.GetRequestBody(CancellationToken.None);
        exchange.Stages.Add("body-read", _time.GetElapsedTime(stage));
        exchange.RequestBytes = body.Length;
        exchange.ForwardedBytes = body.Length;

        if (_capture is not null)
        {
            stage = _time.GetTimestamp();
            if (await CaptureAsync(_capture, captureId, dialect, request.RequestUri, body))
            {
                exchange.CaptureId = captureId;
                e.DataReceived += exchange.OnDataReceived;
                e.UserData = exchange;
            }

            exchange.Stages.Add("capture-write", _time.GetElapsedTime(stage));
        }

        if (_compressionEnabled)
        {
            Apply(e, exchange, await CompressAsync(exchange, dialect, request.ContentType, request.RequestUri.Host, body));
        }

        exchange.Stages.Add("inspector", _time.GetElapsedTime(started));
    }

    /// <summary>Returns the original-content handle when compression stored a request, and picks what a capture keeps of the body.</summary>
    public Task BeforeResponseAsync(object sender, SessionEventArgs e)
    {
        if (e.UserData is not CapturedExchange exchange)
        {
            return Task.CompletedTask;
        }

        var response = e.HttpClient.Response;
        if (exchange.CaptureId is not null)
        {
            exchange.Response = ResponseBuffer.For(response.ContentType, response.Headers.GetFirstHeader("Content-Encoding")?.Value);
        }

        if (exchange.ContentId is { } contentId)
        {
            response.Headers.AddHeader("x-ai-proxy-content-id", contentId);
        }

        return Task.CompletedTask;
    }

    /// <summary>Keeps the response numbers of a captured request; a failure is logged and never reaches the exchange.</summary>
    public async Task AfterResponseAsync(object sender, SessionEventArgs e)
    {
        if (_capture is null || e.UserData is not CapturedExchange { CaptureId: { } captureId } exchange)
        {
            return;
        }

        e.DataReceived -= exchange.OnDataReceived;
        var kept = exchange.Response ?? ResponseBuffer.For(null, null);
        try
        {
            var response = e.HttpClient.Response;
            var stage = _time.GetTimestamp();
            var reading = exchange.Dialect != Dialect.Anthropic
                ? new UsageReading(null, ResponseUsageReader.NoUsage)
                : kept.OverCap
                    ? new UsageReading(null, ResponseUsageReader.OverCap)
                    : ResponseUsageReader.Read(kept.Body, response.ContentType, response.Headers.GetFirstHeader("Content-Encoding")?.Value);
            exchange.Stages.Add("usage-read", _time.GetElapsedTime(stage));
            AddTitaniumTimings(exchange.Stages, e);

            await _capture.StoreResponseAsync(
                new CapturedResponse(
                    captureId,
                    _time.GetUtcNow(),
                    response.StatusCode,
                    exchange.RequestBytes,
                    exchange.ForwardedBytes,
                    kept.Length,
                    reading.Usage,
                    reading.Error,
                    exchange.Stages.ToArray()),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.ResponseCaptureFailed(_logger, e.HttpClient.Request.RequestUri.Host, exception);
        }
        finally
        {
            kept.Release();
            exchange.Response = null;
        }
    }

    /// <summary>Titanium's own measurements of the exchange, prefixed <c>titanium.</c>.</summary>
    private static void AddTitaniumTimings(StageTimings stages, SessionEventArgs e)
    {
        if (e.Timing is not { } timing)
        {
            return;
        }

        Add(stages, "titanium.client-request-read", timing.ClientRequestReadDuration);
        Add(stages, "titanium.connection-wait", timing.ConnectionWaitDuration);
        Add(stages, "titanium.request-send", timing.RequestSendDuration);
        Add(stages, "titanium.ttfb", timing.TimeToFirstByte);
        Add(stages, "titanium.response-delivery", timing.ResponseDeliveryDuration);
        Add(stages, "titanium.total", timing.TotalDuration);
    }

    private static void Add(StageTimings stages, string name, TimeSpan? elapsed)
    {
        if (elapsed is { } measured)
        {
            stages.Add(name, measured);
        }
    }

    private async Task<CompressionResult?> CompressAsync(CapturedExchange exchange, Dialect dialect, string? contentType, string host, byte[] body)
    {
        var stage = _time.GetTimestamp();
        try
        {
            var compressed = await _compression.CompressAsync(new CompressionRequest(body, contentType, dialect), CancellationToken.None);
            exchange.Stages.Add("compression", _time.GetElapsedTime(stage));
            exchange.Stages.Add("compression.", compressed.Stages);
            return compressed;
        }
        catch (Exception exception)
        {
            exchange.Stages.Add("compression", _time.GetElapsedTime(stage));
            Log.CompressionFailed(_logger, host, exception);
            return null;
        }
    }

    /// <summary>Logs the parts that failed and forwards the compressed body when it changed.</summary>
    private void Apply(SessionEventArgs e, CapturedExchange exchange, CompressionResult? compressed)
    {
        if (compressed is null)
        {
            return;
        }

        var host = e.HttpClient.Request.RequestUri.Host;
        foreach (var failure in compressed.PartFailures)
        {
            Log.PartFailed(_logger, failure.ChunkId, failure.ContentType, host, failure.Exception);
        }

        if (!compressed.Changed)
        {
            return;
        }

        var stage = _time.GetTimestamp();
        var forwarded = compressed.Body.ToArray();
        e.SetRequestBody(forwarded);
        exchange.Stages.Add("set-body", _time.GetElapsedTime(stage));
        exchange.ForwardedBytes = forwarded.Length;
        if (compressed.Summary is { } summary)
        {
            exchange.ContentId = summary.RequestId;
            e.UserData = exchange;
        }
    }

    /// <summary>Keeps the body as sent; a failed write is logged and never reaches the request.</summary>
    /// <returns>Whether the request was kept.</returns>
    private async Task<bool> CaptureAsync(IRequestCaptureStore capture, string captureId, Dialect dialect, Uri uri, byte[] body)
    {
        try
        {
            await capture.StoreAsync(
                new CapturedRequest(captureId, _time.GetUtcNow(), dialect, uri.Host, uri.AbsolutePath, body),
                CancellationToken.None);
            return true;
        }
        catch (Exception exception)
        {
            Log.CaptureFailed(_logger, uri.Host, exception);
            return false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1401, Level = LogLevel.Warning, Message = "Compression failed for a request to {ProviderHost}; forwarding the original body")]
        public static partial void CompressionFailed(ILogger logger, string providerHost, Exception exception);

        [LoggerMessage(EventId = 1402, Level = LogLevel.Warning, Message = "The reducer for part {ChunkId} ({ContentType}) of a request to {ProviderHost} failed; the part was forwarded with its original text")]
        public static partial void PartFailed(ILogger logger, int chunkId, string contentType, string providerHost, Exception exception);

        [LoggerMessage(EventId = 1403, Level = LogLevel.Warning, Message = "Capture failed for a request to {ProviderHost}; the request is forwarded unchanged and not captured")]
        public static partial void CaptureFailed(ILogger logger, string providerHost, Exception exception);

        [LoggerMessage(EventId = 1404, Level = LogLevel.Warning, Message = "Capturing the response from {ProviderHost} failed; the response was relayed and its numbers are not kept")]
        public static partial void ResponseCaptureFailed(ILogger logger, string providerHost, Exception exception);
    }
}
