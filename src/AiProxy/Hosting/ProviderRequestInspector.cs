using AiProxy.Core.Capture;
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

    /// <summary>Creates an inspector; with compression disabled and no capture store every request passes through untouched.</summary>
    /// <param name="capture">Where every provider request body is kept as sent; null leaves capture off.</param>
    public ProviderRequestInspector(
        ICompressionPipeline compression,
        IProviderHosts providerHosts,
        bool compressionEnabled,
        ILogger<ProviderRequestInspector> logger,
        IRequestCaptureStore? capture = null)
    {
        ArgumentNullException.ThrowIfNull(compression);
        ArgumentNullException.ThrowIfNull(providerHosts);
        ArgumentNullException.ThrowIfNull(logger);
        _compression = compression;
        _providerHosts = providerHosts;
        _compressionEnabled = compressionEnabled;
        _logger = logger;
        _capture = capture;
    }

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

        var body = await e.GetRequestBody(CancellationToken.None);
        if (_capture is not null)
        {
            await CaptureAsync(_capture, dialect, request.RequestUri, body);
        }

        if (!_compressionEnabled)
        {
            return;
        }

        CompressionResult compressed;
        try
        {
            compressed = await _compression.CompressAsync(
                new CompressionRequest(body, request.ContentType, dialect),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.CompressionFailed(_logger, request.RequestUri.Host, exception);
            return;
        }

        foreach (var failure in compressed.PartFailures)
        {
            Log.PartFailed(_logger, failure.ChunkId, failure.ContentType, request.RequestUri.Host, failure.Exception);
        }

        if (compressed.Changed)
        {
            e.SetRequestBody(compressed.Body.ToArray());
            if (compressed.Summary is { } summary)
            {
                e.UserData = summary.RequestId;
            }
        }
    }

    /// <summary>Returns the original-content handle when compression stored a request.</summary>
    public Task BeforeResponseAsync(object sender, SessionEventArgs e)
    {
        if (e.UserData is string requestId)
        {
            e.HttpClient.Response.Headers.AddHeader("x-ai-proxy-content-id", requestId);
        }

        return Task.CompletedTask;
    }

    /// <summary>Keeps the body as sent; a failed write is logged and never reaches the request.</summary>
    private async Task CaptureAsync(IRequestCaptureStore capture, Dialect dialect, Uri uri, byte[] body)
    {
        try
        {
            await capture.StoreAsync(
                new CapturedRequest(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, dialect, uri.Host, uri.AbsolutePath, body),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.CaptureFailed(_logger, uri.Host, exception);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1401, Level = LogLevel.Warning, Message = "Compression failed for a request to {ProviderHost}; forwarding the original body")]
        public static partial void CompressionFailed(ILogger logger, string providerHost, Exception exception);

        [LoggerMessage(EventId = 1402, Level = LogLevel.Warning, Message = "The reducer for part {ChunkId} ({ContentType}) of a request to {ProviderHost} failed; the part was forwarded with its original text")]
        public static partial void PartFailed(ILogger logger, int chunkId, string contentType, string providerHost, Exception exception);

        [LoggerMessage(EventId = 1402, Level = LogLevel.Warning, Message = "Capture failed for a request to {ProviderHost}; the request is forwarded unchanged and not captured")]
        public static partial void CaptureFailed(ILogger logger, string providerHost, Exception exception);
    }
}
