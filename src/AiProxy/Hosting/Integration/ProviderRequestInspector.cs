using System.Net;
using System.Net.Mime;
using System.Text;
using AiProxy.Ccr;
using AiProxy.Core.Capture;
using AiProxy.Core.Compression;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Gain;
using AiProxy.Core.Providers;
using AiProxy.Core.Timing;
using Microsoft.Net.Http.Headers;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace AiProxy.Hosting.Integration;

/// <summary>Inspects, captures and compresses requests sent to provider API hosts.</summary>
/// <remarks>
///     A zstd body is record-only on every provider host: it is decoded for capture and gain, never compressed.
///     A harness-only host is recorded only when the request or its tunnel carries an ai-proxy credential.
/// </remarks>
/// <param name="compression">Compresses provider bodies; the pass-through pipeline leaves them alone.</param>
/// <param name="providerHosts">Says which hosts are provider APIs.</param>
/// <param name="capture">Where every provider request body is kept as sent; <see cref="NoRequestCapture" /> keeps nothing.</param>
/// <param name="ccr">Plans and relays recoverable compression; <see cref="CcrOff" /> forwards every request.</param>
/// <param name="logger">Where failures are logged.</param>
/// <param name="time">Stamps each capture and times its stages.</param>
public sealed partial class ProviderRequestInspector(
    ICompressionPipeline compression,
    IProviderHosts providerHosts,
    IRequestCaptureStore capture,
    ICcrRuntime ccr,
    ILogger<ProviderRequestInspector> logger,
    TimeProvider time,
    IGainStore gain)
{
    private static readonly HttpHeader[] PlainTextHeaders = [new(HeaderNames.ContentType, MediaTypeNames.Text.Plain)];
    private static readonly HttpHeader[] JsonHeaders = [new(HeaderNames.ContentType, MediaTypeNames.Application.Json)];

    private const long MaxInspectedBodyBytes = 8 * 1024 * 1024;

    /// <summary>The Codex Responses path; a WebSocket upgrade to it on a harness-only host gets 426, so Codex falls back to HTTPS.</summary>
    private const string CodexResponsesPath = "/backend-api/codex/responses";

    private const string UpgradeRequired = "ai-proxy records this host over HTTPS only; retry without a WebSocket.\n";
    private const string ContentEncodingHeader = "Content-Encoding";

    /// <summary>Whether capture is on, so the proxy has to time exchanges and hand over each finished response.</summary>
    internal bool CapturesResponses => capture.Keeps;

    /// <summary>Leaves unknown hosts untouched and processes known provider requests.</summary>
    public async Task BeforeRequestAsync(SessionEventArgs e)
    {
        var request = e.HttpClient.Request;
        var caller = ExtractCaller(e.HttpClient);
        if (providerHosts.Find(request.RequestUri.Host) is not ProviderHost provider || !PassesHarnessOnlyRules(e, provider, caller) || !Inspects(request, provider))
        {
            return;
        }

        var started = time.GetTimestamp();
        var exchange = CreateExchange(request, provider, caller.Project);
        var body = await ReadBodyAsync(e, exchange);
        var encoded = IsZstd(request);
        var content = encoded ? Decode(exchange, request.RequestUri.Host, body) : body;
        await CaptureRequestAsync(e, exchange, provider.Dialect, content);
        if (compression.Compresses && !provider.HarnessOnly && !encoded)
        {
            await CompressRequestAsync(e, exchange, provider, body);
        }
        else
        {
            await RecordGainAsync(exchange, request.Method, content, content, GainLabels.Rules.Unchanged, passthrough: true);
        }

        exchange.Stages.Add(Stages.Inspector, time.GetElapsedTime(started));
    }

    /// <summary>Reads the caller's project and credentials, then strips those headers so they never reach any upstream.</summary>
    private static Caller ExtractCaller(HttpWebClient client)
    {
        var headers = client.Request.Headers;
        var requestCredential = ProjectCredential.Read(headers.GetFirstHeader(HeaderNames.ProxyAuthorization)?.Value);
        var connectCredential = ProjectCredential.Read(client.ConnectRequest?.Headers.GetFirstHeader(HeaderNames.ProxyAuthorization)?.Value);
        var project = ProjectCredential.Resolve(headers.GetFirstHeader(GainApi.ProjectHeader)?.Value, requestCredential, connectCredential);
        headers.RemoveHeader(GainApi.ProjectHeader);
        headers.RemoveHeader(HeaderNames.ProxyAuthorization);
        return new Caller(project, requestCredential is not null || connectCredential is not null);
    }

    /// <summary>Whether a provider request is inspected: a harness-only host needs a credential and refuses a Codex WebSocket with 426.</summary>
    private bool PassesHarnessOnlyRules(SessionEventArgs e, ProviderHost provider, Caller caller)
    {
        var request = e.HttpClient.Request;
        switch (provider.HarnessOnly)
        {
            case true when !caller.Credentialed:
                return false;
            case true when request.UpgradeToWebSocket && request.RequestUri.AbsolutePath.TrimEnd('/').Equals(CodexResponsesPath, StringComparison.OrdinalIgnoreCase):
                Log.WebSocketRefused(logger, request.RequestUri.Host);
                e.GenericResponse(UpgradeRequired, HttpStatusCode.UpgradeRequired, PlainTextHeaders, true);
                return false;
            default:
                return true;
        }
    }

    /// <summary>Whether the body is read: it must exist, fit the size cap and, on a harness-only host, stay byte-identical once read.</summary>
    private static bool Inspects(Request request, ProviderHost provider)
    {
        if (!request.HasBody || request.ContentLength is < 0 or > MaxInspectedBodyBytes)
        {
            return false;
        }

        // Reading a gzip or br body would make Titanium decode and re-encode it, which changes the forwarded bytes.
        var contentEncoding = request.Headers.GetFirstHeader(HeaderNames.ContentEncoding)?.Value;
        return !provider.HarnessOnly
               || ZstdRequestBody.IsZstd(contentEncoding)
               || string.IsNullOrWhiteSpace(contentEncoding)
               || contentEncoding.Trim().Equals("identity", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsZstd(Request request) => ZstdRequestBody.IsZstd(request.Headers.GetFirstHeader(HeaderNames.ContentEncoding)?.Value);

    private static CapturedExchange CreateExchange(Request request, ProviderHost provider, string? project) => new()
    {
        Dialect = provider.Dialect,
        GainProject = project,
        Host = request.RequestUri.Host,
        IsModelCall = ProviderEndpoints.IsModelCall(provider.Dialect, request.Method, request.RequestUri.AbsolutePath)
    };

    /// <summary>Reads the request body, records its size and attaches the exchange to the session.</summary>
    private async Task<byte[]> ReadBodyAsync(SessionEventArgs e, CapturedExchange exchange)
    {
        var stage = time.GetTimestamp();
        var body = await e.GetRequestBody(CancellationToken.None);
        exchange.Stages.Add(Stages.BodyRead, time.GetElapsedTime(stage));
        exchange.RequestBytes = body.Length;
        exchange.ForwardedBytes = body.Length;
        e.UserData = exchange;
        return body;
    }

    /// <summary>Keeps the decoded body when capture is on, and subscribes the exchange to the response it captures.</summary>
    private async Task CaptureRequestAsync(SessionEventArgs e, CapturedExchange exchange, Dialect dialect, byte[] content)
    {
        if (!capture.Keeps)
        {
            return;
        }

        var stage = time.GetTimestamp();
        var captureId = Guid.CreateVersion7().ToString("N");
        if (await CaptureAsync(captureId, dialect, e.HttpClient.Request.RequestUri, content))
        {
            exchange.CaptureId = captureId;
            e.DataReceived += exchange.OnDataReceived;
        }

        exchange.Stages.Add(Stages.CaptureWrite, time.GetElapsedTime(stage));
    }

    /// <summary>Plans recoverable compression, then relays or forwards the compressed body and records its gain.</summary>
    private async Task CompressRequestAsync(SessionEventArgs e, CapturedExchange exchange, ProviderHost provider, byte[] body)
    {
        var request = e.HttpClient.Request;
        var plan = await ccr.DecideAsync(provider.Dialect, provider.ChatCcr, request.Method, request.RequestUri.AbsolutePath, body, CancellationToken.None);
        if (plan.Failure is { } failure)
        {
            await RefuseUnplannedAsync(e, exchange, failure, body);
            return;
        }

        var compressed = await CompressAsync(exchange, provider.Dialect, request.ContentType, request.RequestUri.Host, body, plan.Mode);
        if (plan.Relays)
        {
            await RelayAsync(e, exchange, plan, body, compressed);
            return;
        }

        var forwarded = Forwarded(body, compressed);
        Apply(e, exchange, compressed, forwarded);
        await RecordGainAsync(exchange, request.Method, body, forwarded, compressed?.Summary?.Path ?? GainLabels.Rules.Unchanged);
    }

    /// <summary>Answers 529 when the CCR ledger cannot be read, so the request is never forwarded without its plan.</summary>
    private async Task RefuseUnplannedAsync(SessionEventArgs e, CapturedExchange exchange, Exception failure, byte[] body)
    {
        var request = e.HttpClient.Request;
        Log.LedgerLookupFailed(logger, request.RequestUri.Host, failure);
        exchange.GainFailures.Add(new GainFailure(time.GetUtcNow(), exchange.GainId, exchange.GainProject, Stages.CcrPlanning, failure.GetType().Name));
        await RecordGainAsync(exchange, request.Method, body, body, GainLabels.Rules.CcrPlanFailed);
        e.GenericResponse(CcrPlan.LedgerUnavailable, (HttpStatusCode)529, JsonHeaders, true);
    }

    /// <summary>The project a request is billed to, and whether it carried an ai-proxy credential.</summary>
    private sealed record Caller(string? Project, bool Credentialed);

    /// <summary>Returns the original-content handle when compression stored a request, and picks what a capture keeps of the body.</summary>
    public static Task BeforeResponseAsync(SessionEventArgs e)
    {
        if (e.UserData is not CapturedExchange exchange || exchange.Relayed)
        {
            return Task.CompletedTask;
        }

        var response = e.HttpClient.Response;
        if (exchange.CaptureId is not null)
        {
            exchange.Response = ResponseBuffer.For(response.ContentType ?? string.Empty, response.Headers.GetFirstHeader(ContentEncodingHeader)?.Value ?? string.Empty);
        }

        if (exchange.ContentId is { } contentId)
        {
            response.Headers.AddHeader(CcrRelay.ContentIdHeader, contentId);
        }

        return Task.CompletedTask;
    }

    /// <summary>Keeps the response numbers of a captured request; a failure is logged and never reaches the exchange.</summary>
    public async Task AfterResponseAsync(SessionEventArgs e)
    {
        if (!capture.Keeps || e.UserData is not CapturedExchange { CaptureId: { } captureId } exchange)
        {
            return;
        }

        e.DataReceived -= exchange.OnDataReceived;
        var kept = exchange.Response ?? ResponseBuffer.Create();
        try
        {
            var response = e.HttpClient.Response;
            var stage = time.GetTimestamp();
            var reading = kept.OverCap
                ? new UsageReading(null, ResponseUsageReader.OverCap)
                : ResponseUsageReader.Read(kept.Body, response.ContentType, response.Headers.GetFirstHeader(ContentEncodingHeader)?.Value, exchange.Dialect);
            exchange.Stages.Add(Stages.UsageRead, time.GetElapsedTime(stage));
            AddTitaniumTimings(exchange.Stages, e);

            await capture.StoreResponseAsync(
                new CapturedResponse(
                    captureId,
                    time.GetUtcNow(),
                    response.StatusCode,
                    exchange.RequestBytes,
                    exchange.ForwardedBytes,
                    kept.Length,
                    reading.Usage,
                    reading.Error,
                    exchange.Stages.ToArray())
                {
                    MessageId = reading.MessageId
                },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.ResponseCaptureFailed(logger, e.HttpClient.Request.RequestUri.Host, exception);
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

        Add(stages, Stages.TitaniumClientRequestRead, timing.ClientRequestReadDuration);
        Add(stages, Stages.TitaniumConnectionWait, timing.ConnectionWaitDuration);
        Add(stages, Stages.TitaniumRequestSend, timing.RequestSendDuration);
        Add(stages, Stages.TitaniumTimeToFirstByte, timing.TimeToFirstByte);
        Add(stages, Stages.TitaniumResponseDelivery, timing.ResponseDeliveryDuration);
        Add(stages, Stages.TitaniumTotal, timing.TotalDuration);
    }

    private static void Add(StageTimings stages, string name, TimeSpan? elapsed)
    {
        if (elapsed is { } measured)
        {
            stages.Add(name, measured);
        }
    }

    private async Task<CompressionResult?> CompressAsync(CapturedExchange exchange, Dialect dialect, string? contentType, string host, byte[] body, CcrMode mode)
    {
        var stage = time.GetTimestamp();
        try
        {
            var compressed = await compression.CompressAsync(new CompressionRequest(body, contentType, dialect) { Ccr = mode }, CancellationToken.None);
            if (compressed.Failure is not null)
            {
                exchange.GainFailures.Add(new GainFailure(time.GetUtcNow(), exchange.GainId, exchange.GainProject, Stages.ContentStore, "content_store_failed"));
            }

            if (compressed is { Changed: true, Summary: { } summary })
            {
                Log.Compressed(logger, summary.Path, summary.ChunkCount, body.Length, compressed.Body.Length,
                    body.Length == 0 ? 1 : (double)compressed.Body.Length / body.Length);
            }

            exchange.Stages.Add(Stages.Compression, time.GetElapsedTime(stage));
            exchange.Stages.Add(Stages.CompressionPrefix, compressed.Stages);
            return compressed;
        }
        catch (Exception exception)
        {
            exchange.Stages.Add(Stages.Compression, time.GetElapsedTime(stage));
            Log.CompressionFailed(logger, host, exception);
            exchange.GainFailures.Add(new GainFailure(time.GetUtcNow(), exchange.GainId, exchange.GainProject, Stages.Compression, exception.GetType().Name));
            return null;
        }
    }

    /// <summary>Forwards the request through the CCR relay with the tool appended and the thread pointed at its last message.</summary>
    private async Task RelayAsync(SessionEventArgs e, CapturedExchange exchange, CcrPlan plan, byte[] body, CompressionResult? compressed)
    {
        LogFailures(e, compressed);
        var forwarded = Forwarded(body, compressed);
        if (compressed is { Changed: true, Summary: { } summary })
        {
            exchange.ContentId = summary.RequestId;
        }

        var toolCharacters = 0;
        switch (plan.Action)
        {
            case CcrAction.RelayWithTool when plan.WithTool(forwarded) is { } withTool:
                toolCharacters = withTool.ToolCharacters;
                forwarded = withTool.Body;
                break;
            case CcrAction.RelayContinue:
                forwarded = CcrRequest.ContinueFrom(forwarded, plan.ContinueFrom);
                break;
        }

        exchange.ForwardedBytes = forwarded.Length;
        var rule = compressed?.Summary?.Path ?? (toolCharacters > 0 ? GainLabels.Rules.CcrTool : GainLabels.Rules.Unchanged);
        await RecordGainAsync(exchange, e.HttpClient.Request.Method, body, forwarded, rule, toolCharacters);
        e.UserData = exchange;
        await ccr.RelayAsync(e, exchange, forwarded, plan, compressed?.Originals ?? new Dictionary<string, string>());
    }

    private void LogFailures(SessionEventArgs e, CompressionResult? compressed)
    {
        foreach (var failure in compressed?.PartFailures ?? [])
        {
            Log.PartFailed(logger, failure.ChunkId, failure.ContentType, e.HttpClient.Request.RequestUri.Host, failure.Exception);
            if (e.UserData is CapturedExchange exchange)
            {
                exchange.GainFailures.Add(new GainFailure(time.GetUtcNow(), exchange.GainId, exchange.GainProject, failure.ContentType, failure.Exception.GetType().Name));
            }
        }
    }

    /// <summary>The body to forward: the compressed one when compression changed it, else the body as sent.</summary>
    private static byte[] Forwarded(byte[] body, CompressionResult? compressed) => compressed is { Changed: true } ? BodyBytes.Array(compressed.Body) : body;

    /// <summary>Logs the parts that failed and forwards the compressed body when it changed.</summary>
    private void Apply(SessionEventArgs e, CapturedExchange exchange, CompressionResult? compressed, byte[] forwarded)
    {
        if (compressed is null)
        {
            return;
        }

        LogFailures(e, compressed);

        if (!compressed.Changed)
        {
            return;
        }

        var stage = time.GetTimestamp();
        e.SetRequestBody(forwarded);
        exchange.Stages.Add(Stages.SetBody, time.GetElapsedTime(stage));
        exchange.ForwardedBytes = forwarded.Length;
        if (compressed.Summary is not { } summary)
        {
            return;
        }

        exchange.ContentId = summary.RequestId;
        e.UserData = exchange;
    }

    /// <summary>Keeps the body as sent; a failed write is logged and never reaches the request.</summary>
    /// <returns>Whether the request was kept.</returns>
    private async Task<bool> CaptureAsync(string captureId, Dialect dialect, Uri uri, byte[] body)
    {
        try
        {
            await capture.StoreAsync(
                new CapturedRequest(captureId, time.GetUtcNow(), dialect, uri.Host, uri.AbsolutePath, body),
                CancellationToken.None);
            return true;
        }
        catch (Exception exception)
        {
            Log.CaptureFailed(logger, uri.Host, exception);
            return false;
        }
    }

    /// <summary>
    ///     A zstd body decoded for capture and measurement. A body that does not decode, for any reason, is logged,
    ///     noted as a gain failure and used as sent; forwarding never depends on it.
    /// </summary>
    private byte[] Decode(CapturedExchange exchange, string host, byte[] body)
    {
        var stage = time.GetTimestamp();
        try
        {
            return ZstdRequestBody.Decode(body);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            Log.ZstdDecodeFailed(logger, host, exception);
            exchange.GainFailures.Add(new GainFailure(time.GetUtcNow(), exchange.GainId, exchange.GainProject, Stages.Zstd, exception.GetType().Name));
            return body;
        }
        finally
        {
            exchange.Stages.Add(Stages.ZstdDecode, time.GetElapsedTime(stage));
        }
    }

    private async Task RecordGainAsync(CapturedExchange exchange, string method, byte[] original, byte[] forwarded, string rule, int toolCharacters = 0, bool passthrough = false)
    {
        if (!exchange.IsModelCall)
        {
            await RecordOtherAsync(exchange, method, original.Length);
            return;
        }

        exchange.GainRule = rule;
        exchange.GainMethod = method;
        exchange.GainLayer = GainLabels.Layers.Of(rule);
        exchange.GainPipeline = compression.Compresses && !passthrough ? GainLabels.Pipelines.Content : GainLabels.Pipelines.Passthrough;
        exchange.GainAt = time.GetUtcNow();
        try
        {
            await gain.RecordAsync(new GainHistoryEntry(exchange.GainId, exchange.GainAt, exchange.GainProject,
                    exchange.Dialect.ToString().ToLowerInvariant(), method, rule, exchange.GainLayer, exchange.GainPipeline,
                    GainReportBuilder.RequestTotals(Encoding.UTF8.GetCharCount(original), Encoding.UTF8.GetCharCount(forwarded), original.Length, forwarded.Length, toolCharacters), exchange.Host),
                exchange.GainFailures, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.GainWriteFailed(logger, exchange.GainId, exception);
        }
    }

    private async Task RecordOtherAsync(CapturedExchange exchange, string method, long bytes)
    {
        try
        {
            await gain.RecordOtherAsync(new GainOtherEntry(exchange.GainId, time.GetUtcNow(), exchange.GainProject,
                exchange.Dialect.ToString().ToLowerInvariant(), method, bytes), CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.GainWriteFailed(logger, exchange.GainId, exception);
        }
    }

    /// <summary>Names of the timed stages and gain-failure stages this inspector records.</summary>
    private static class Stages
    {
        public const string Inspector = "inspector";
        public const string BodyRead = "body-read";
        public const string CaptureWrite = "capture-write";
        public const string UsageRead = "usage-read";
        public const string Compression = "compression";
        public const string CompressionPrefix = "compression.";
        public const string SetBody = "set-body";
        public const string ZstdDecode = "zstd-decode";
        public const string TitaniumClientRequestRead = "titanium.client-request-read";
        public const string TitaniumConnectionWait = "titanium.connection-wait";
        public const string TitaniumRequestSend = "titanium.request-send";
        public const string TitaniumTimeToFirstByte = "titanium.ttfb";
        public const string TitaniumResponseDelivery = "titanium.response-delivery";
        public const string TitaniumTotal = "titanium.total";
        public const string CcrPlanning = "ccr-plan";
        public const string ContentStore = "content-store";
        public const string Zstd = "zstd";
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1407, Level = LogLevel.Information, Message = "compressed the body with {Path} over {ChunkCount} parts, {OriginalBytes} bytes to {CompressedBytes}, ratio {Ratio:F3}")]
        public static partial void Compressed(ILogger logger, string path, int chunkCount, int originalBytes, int compressedBytes, double ratio);

        [LoggerMessage(EventId = 1406, Level = LogLevel.Warning, Message = "Gain metadata for request {RequestId} could not be recorded")]
        public static partial void GainWriteFailed(ILogger logger, string requestId, Exception exception);

        [LoggerMessage(EventId = 1401, Level = LogLevel.Warning, Message = "Compression failed for a request to {ProviderHost}; forwarding the original body")]
        public static partial void CompressionFailed(ILogger logger, string providerHost, Exception exception);

        [LoggerMessage(EventId = 1402, Level = LogLevel.Warning, Message = "The reducer for part {ChunkId} ({ContentType}) of a request to {ProviderHost} failed; the part was forwarded with its original text")]
        public static partial void PartFailed(ILogger logger, int chunkId, string contentType, string providerHost, Exception exception);

        [LoggerMessage(EventId = 1403, Level = LogLevel.Warning, Message = "Capture failed for a request to {ProviderHost}; the request is forwarded unchanged and not captured")]
        public static partial void CaptureFailed(ILogger logger, string providerHost, Exception exception);

        [LoggerMessage(EventId = 1404, Level = LogLevel.Warning, Message = "Capturing the response from {ProviderHost} failed; the response was relayed and its numbers are not kept")]
        public static partial void ResponseCaptureFailed(ILogger logger, string providerHost, Exception exception);

        [LoggerMessage(EventId = 1408, Level = LogLevel.Warning, Message = "The zstd body of a request to {ProviderHost} did not decode; it was forwarded as sent and measured undecoded")]
        public static partial void ZstdDecodeFailed(ILogger logger, string providerHost, Exception exception);

        [LoggerMessage(EventId = 1409, Level = LogLevel.Information, Message = "Answered 426 to a WebSocket upgrade to {ProviderHost}, so the client falls back to HTTPS the proxy can record")]
        public static partial void WebSocketRefused(ILogger logger, string providerHost);

        [LoggerMessage(EventId = 1405, Level = LogLevel.Error, Message = "The CCR thread ledger could not be read for a request to {ProviderHost}; the client was told to retry")]
        public static partial void LedgerLookupFailed(ILogger logger, string providerHost, Exception exception);
    }
}
