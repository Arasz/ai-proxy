using AiProxy.Core.Compression;
using AiProxy.Core.Providers;
using AiProxy.Core.Routing;
using AiProxy.Forwarding;

namespace AiProxy.Hosting;

/// <summary>Detects the provider, routes the request, and hands it to the forwarder.</summary>
public sealed partial class ProxyForwardingMiddleware
{
    /// <summary>The largest request body the proxy reads into memory to look at.</summary>
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    /// <summary>The header that carries the id the original of the request is kept under.</summary>
    private const string ContentIdHeader = "x-ai-proxy-content-id";

    private readonly RequestDelegate _next;
    private readonly CompositeDialectDetector _detector;
    private readonly IRequestRouter _router;
    private readonly ICompressionPipeline _compression;
    private readonly IUpstreamForwarder _forwarder;
    private readonly ILogger<ProxyForwardingMiddleware> _logger;

    /// <summary>Holds the collaborators the proxy is composed of.</summary>
    public ProxyForwardingMiddleware(
        RequestDelegate next,
        CompositeDialectDetector detector,
        IRequestRouter router,
        ICompressionPipeline compression,
        IUpstreamForwarder forwarder,
        ILogger<ProxyForwardingMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(compression);
        ArgumentNullException.ThrowIfNull(forwarder);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _detector = detector;
        _router = router;
        _compression = compression;
        _forwarder = forwarder;
        _logger = logger;
    }

    /// <summary>Proxies the request, or refuses it.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint() is not null)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        var headers = SnapshotHeaders(context.Request);

        // The body is read only when it is needed. A path an operator named for another
        // server is carried without the proxy ever looking at the bytes.
        var withoutBody = _detector.Detect(new DetectionRequest(path, headers, null));
        var preliminary = _router.Route(new RoutingRequest(path, headers, null, withoutBody));

        if (preliminary is RouteDecision.PassThroughRoute passThrough)
        {
            Log.PassThrough(_logger, path, passThrough.Upstream.Host);
            await _forwarder.ForwardAsync(
                context,
                Call(passThrough, context, headers, body: null, bodyStream: context.Request.Body),
                context.RequestAborted);
            return;
        }

        var body = await ReadBodyAsync(context);
        if (body is null)
        {
            Log.BodyTooLarge(_logger, path);
            await WriteRefusalAsync(
                context,
                new RouteDecision.RefusedRoute(
                    RefusalReason.Unknown,
                    $"the body of path {path} is larger than the {MaxBodyBytes / (1024 * 1024)} MiB the proxy reads, so no provider could be named"));
            return;
        }

        var detection = _detector.Detect(new DetectionRequest(path, headers, System.Text.Encoding.UTF8.GetString(body)));
        var route = _router.Route(new RoutingRequest(path, headers, System.Text.Encoding.UTF8.GetString(body), detection));

        switch (route)
        {
            case RouteDecision.RefusedRoute refusal:
                Log.Refused(_logger, path, refusal.Code, refusal.Reason);
                await WriteRefusalAsync(context, refusal);
                return;

            case RouteDecision.ProviderRoute provider:
            {
                var contentType = context.Request.ContentType;
                var compressed = await _compression.CompressAsync(
                    new CompressionRequest(body, contentType, provider.Dialect),
                    context.RequestAborted);

                if (compressed.Changed && compressed.Summary is { } summary)
                {
                    context.Response.Headers[ContentIdHeader] = summary.RequestId;
                    Log.Compressed(
                        _logger,
                        summary.Path,
                        summary.ChunkCount,
                        body.Length,
                        compressed.Body.Length,
                        summary.Ratio);
                }

                if (compressed.Failure is { } failure)
                {
                    Log.StoreFailed(_logger, failure);
                }

                Log.Proxied(_logger, path, provider.Dialect.ToString(), provider.Upstream.Host, provider.Credential.ToString());
                await _forwarder.ForwardAsync(
                    context,
                    Call(provider, context, headers, compressed.Body, contentType),
                    context.RequestAborted);
                return;
            }

            default:
                await _next(context);
                return;
        }
    }

    private static UpstreamCall Call(
        RouteDecision.PassThroughRoute route,
        HttpContext context,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlyMemory<byte>? body,
        Stream? bodyStream) =>
        new()
        {
            Upstream = route.Upstream,
            PathAndQuery = context.Request.Path + context.Request.QueryString,
            Method = context.Request.Method,
            Headers = headers,
            Body = body,
            BodyStream = bodyStream,
            ContentType = context.Request.ContentType,
        };

    private static UpstreamCall Call(
        RouteDecision.ProviderRoute route,
        HttpContext context,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlyMemory<byte> body,
        string? contentType) =>
        new()
        {
            Upstream = route.Upstream,
            PathAndQuery = context.Request.Path + context.Request.QueryString,
            Method = context.Request.Method,
            Headers = headers,
            Body = body,
            ContentType = contentType,
            Dialect = route.Dialect,
            Credential = route.Credential,
        };

    private static IReadOnlyDictionary<string, string> SnapshotHeaders(HttpRequest request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in request.Headers)
        {
            headers[header.Key] = header.Value.ToString();
        }

        return headers;
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength is long length && length > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;

        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task WriteRefusalAsync(HttpContext context, RouteDecision.RefusedRoute refusal)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(
            new
            {
                type = $"https://ai-proxy.local/problems/{refusal.Code}",
                title = refusal.Code,
                status = StatusCodes.Status404NotFound,
                detail = refusal.Reason,
                code = refusal.Code,
            },
            context.RequestAborted);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1101, Level = LogLevel.Information,
            Message = "proxied {Path} to {Upstream} as {Dialect} with the {Credential} credential")]
        public static partial void Proxied(ILogger logger, string path, string dialect, string upstream, string credential);

        [LoggerMessage(EventId = 1102, Level = LogLevel.Information,
            Message = "carried {Path} to {Upstream} unchanged")]
        public static partial void PassThrough(ILogger logger, string path, string upstream);

        [LoggerMessage(EventId = 1103, Level = LogLevel.Warning,
            Message = "refused {Path} with {Code}: {Reason}")]
        public static partial void Refused(ILogger logger, string path, string code, string reason);

        [LoggerMessage(EventId = 1104, Level = LogLevel.Warning,
            Message = "the body of {Path} is too large for the proxy to look at")]
        public static partial void BodyTooLarge(ILogger logger, string path);

        [LoggerMessage(EventId = 1105, Level = LogLevel.Information,
            Message = "compressed the body with {Path} over {ChunkCount} parts, {OriginalBytes} bytes to {CompressedBytes}, ratio {Ratio:F3}")]
        public static partial void Compressed(
            ILogger logger,
            string path,
            int chunkCount,
            int originalBytes,
            int compressedBytes,
            double ratio);

        [LoggerMessage(EventId = 1106, Level = LogLevel.Warning,
            Message = "the compressed body could not be kept: {Failure}")]
        public static partial void StoreFailed(ILogger logger, string failure);
    }
}
