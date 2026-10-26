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
using Titanium.Web.Proxy.Models;

namespace AiProxy.Hosting;

/// <summary>Inspects, captures and compresses requests sent to provider API hosts.</summary>
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
    private const long MaxInspectedBodyBytes = 8 * 1024 * 1024;

    /// <summary>Whether capture is on, so the proxy has to time exchanges and hand over each finished response.</summary>
    internal bool CapturesResponses => capture.Keeps;

    /// <summary>Leaves unknown hosts untouched and processes known provider requests.</summary>
    public async Task BeforeRequestAsync(object sender, SessionEventArgs e)
    {
        var request = e.HttpClient.Request;
        var project = request.Headers.GetFirstHeader(GainApi.ProjectHeader)?.Value;
        request.Headers.RemoveHeader(GainApi.ProjectHeader);
        if (providerHosts.Find(request.RequestUri.Host) is not { } dialect || !request.HasBody) return;

        if (request.ContentLength is < 0 or > MaxInspectedBodyBytes) return;

        var started = time.GetTimestamp();
        var exchange = new CapturedExchange { Dialect = dialect, GainProject = project };
        var captureId = Guid.CreateVersion7().ToString("N");

        var stage = time.GetTimestamp();
        var body = await e.GetRequestBody(CancellationToken.None);
        exchange.Stages.Add("body-read", time.GetElapsedTime(stage));
        exchange.RequestBytes = body.Length;
        exchange.ForwardedBytes = body.Length;
        e.UserData = exchange;

        if (capture.Keeps)
        {
            stage = time.GetTimestamp();
            if (await CaptureAsync(captureId, dialect, request.RequestUri, body))
            {
                exchange.CaptureId = captureId;
                e.DataReceived += exchange.OnDataReceived;
                e.UserData = exchange;
            }

            exchange.Stages.Add("capture-write", time.GetElapsedTime(stage));
        }

        if (compression.Compresses)
        {
            var plan = await ccr.DecideAsync(dialect, request.Method, request.RequestUri.AbsolutePath, body, CancellationToken.None);
            if (plan.Failure is { } failure)
            {
                Log.LedgerLookupFailed(logger, request.RequestUri.Host, failure);
                exchange.GainFailures.Add(new GainFailure(time.GetUtcNow(), exchange.GainId, project, "ccr-plan", failure.GetType().Name));
                await RecordGainAsync(exchange, request.Method, body, body, GainLabels.Rules.CcrPlanFailed);
                e.GenericResponse(
                    CcrPlan.LedgerUnavailable,
                    (HttpStatusCode)529,
                    [new HttpHeader(HeaderNames.ContentType, MediaTypeNames.Application.Json)],
                    true);
                return;
            }

            var compressed = await CompressAsync(exchange, dialect, request.ContentType, request.RequestUri.Host, body, plan.Mode);
            if (plan.Relays)
            {
                await RelayAsync(e, exchange, plan, body, compressed);
            }
            else
            {
                Apply(e, exchange, compressed);
                await RecordGainAsync(exchange, request.Method, body,
                    compressed is { Changed: true } ? compressed.Body.ToArray() : body, compressed?.Summary?.Path ?? GainLabels.Rules.Unchanged);
            }
        }
        else
        {
            await RecordGainAsync(exchange, request.Method, body, body, GainLabels.Rules.Unchanged);
        }

        exchange.Stages.Add("inspector", time.GetElapsedTime(started));
    }

    /// <summary>Returns the original-content handle when compression stored a request, and picks what a capture keeps of the body.</summary>
    public Task BeforeResponseAsync(object sender, SessionEventArgs e)
    {
        if (e.UserData is not CapturedExchange exchange) return Task.CompletedTask;

        if (exchange.Relayed) return Task.CompletedTask;

        var response = e.HttpClient.Response;
        if (exchange.CaptureId is not null) exchange.Response = ResponseBuffer.For(response.ContentType, response.Headers.GetFirstHeader("Content-Encoding")?.Value);

        if (exchange.ContentId is { } contentId) response.Headers.AddHeader(CcrRelay.ContentIdHeader, contentId);

        return Task.CompletedTask;
    }

    /// <summary>Keeps the response numbers of a captured request; a failure is logged and never reaches the exchange.</summary>
    public async Task AfterResponseAsync(object sender, SessionEventArgs e)
    {
        if (!capture.Keeps || e.UserData is not CapturedExchange { CaptureId: { } captureId } exchange) return;

        e.DataReceived -= exchange.OnDataReceived;
        var kept = exchange.Response ?? ResponseBuffer.For(null, null);
        try
        {
            var response = e.HttpClient.Response;
            var stage = time.GetTimestamp();
            var reading = exchange.Dialect != Dialect.Anthropic
                ? new UsageReading(null, ResponseUsageReader.NoUsage)
                : kept.OverCap
                    ? new UsageReading(null, ResponseUsageReader.OverCap)
                    : ResponseUsageReader.Read(kept.Body, response.ContentType, response.Headers.GetFirstHeader("Content-Encoding")?.Value);
            exchange.Stages.Add("usage-read", time.GetElapsedTime(stage));
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
                    exchange.Stages.ToArray()),
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
        if (e.Timing is not { } timing) return;

        Add(stages, "titanium.client-request-read", timing.ClientRequestReadDuration);
        Add(stages, "titanium.connection-wait", timing.ConnectionWaitDuration);
        Add(stages, "titanium.request-send", timing.RequestSendDuration);
        Add(stages, "titanium.ttfb", timing.TimeToFirstByte);
        Add(stages, "titanium.response-delivery", timing.ResponseDeliveryDuration);
        Add(stages, "titanium.total", timing.TotalDuration);
    }

    private static void Add(StageTimings stages, string name, TimeSpan? elapsed)
    {
        if (elapsed is { } measured) stages.Add(name, measured);
    }

    private async Task<CompressionResult?> CompressAsync(CapturedExchange exchange, Dialect dialect, string? contentType, string host, byte[] body, CcrMode mode)
    {
        var stage = time.GetTimestamp();
        try
        {
            var compressed = await compression.CompressAsync(new CompressionRequest(body, contentType, dialect) { Ccr = mode }, CancellationToken.None);
            if (compressed.Failure is not null) exchange.GainFailures.Add(new GainFailure(time.GetUtcNow(), exchange.GainId, exchange.GainProject, "content-store", "content_store_failed"));
            if (compressed is { Changed: true, Summary: { } summary })
                Log.Compressed(logger, summary.Path, summary.ChunkCount, body.Length, compressed.Body.Length,
                    body.Length == 0 ? 1 : (double)compressed.Body.Length / body.Length);
            exchange.Stages.Add("compression", time.GetElapsedTime(stage));
            exchange.Stages.Add("compression.", compressed.Stages);
            return compressed;
        }
        catch (Exception exception)
        {
            exchange.Stages.Add("compression", time.GetElapsedTime(stage));
            Log.CompressionFailed(logger, host, exception);
            exchange.GainFailures.Add(new GainFailure(time.GetUtcNow(), exchange.GainId, exchange.GainProject, "compression", exception.GetType().Name));
            return null;
        }
    }

    /// <summary>Forwards the request through the CCR relay with the tool appended and the thread pointed at its last message.</summary>
    private async Task RelayAsync(SessionEventArgs e, CapturedExchange exchange, CcrPlan plan, byte[] body, CompressionResult? compressed)
    {
        LogFailures(e, compressed);
        var forwarded = compressed is { Changed: true } ? compressed.Body.ToArray() : body;
        if (compressed is { Changed: true, Summary: { } summary }) exchange.ContentId = summary.RequestId;

        if (plan.Action == CcrAction.RelayWithTool && CcrRequest.AppendTool(forwarded) is { } withTool) forwarded = withTool;

        if (plan.Action == CcrAction.RelayContinue) forwarded = CcrRequest.ContinueFrom(forwarded, plan.ContinueFrom);

        exchange.ForwardedBytes = forwarded.Length;
        await RecordGainAsync(exchange, e.HttpClient.Request.Method, body, forwarded, compressed?.Summary?.Path ?? GainLabels.Rules.Unchanged);
        e.UserData = exchange;
        await ccr.RelayAsync(e, exchange, forwarded, plan, compressed?.Originals ?? new Dictionary<string, string>());
    }

    private void LogFailures(SessionEventArgs e, CompressionResult? compressed)
    {
        foreach (var failure in compressed?.PartFailures ?? [])
        {
            Log.PartFailed(logger, failure.ChunkId, failure.ContentType, e.HttpClient.Request.RequestUri.Host, failure.Exception);
            if (e.UserData is CapturedExchange exchange) exchange.GainFailures.Add(new GainFailure(time.GetUtcNow(), exchange.GainId, exchange.GainProject, failure.ContentType, failure.Exception.GetType().Name));
        }
    }

    /// <summary>Logs the parts that failed and forwards the compressed body when it changed.</summary>
    private void Apply(SessionEventArgs e, CapturedExchange exchange, CompressionResult? compressed)
    {
        if (compressed is null) return;

        LogFailures(e, compressed);

        if (!compressed.Changed) return;

        var stage = time.GetTimestamp();
        var forwarded = compressed.Body.ToArray();
        e.SetRequestBody(forwarded);
        exchange.Stages.Add("set-body", time.GetElapsedTime(stage));
        exchange.ForwardedBytes = forwarded.Length;
        if (compressed.Summary is { } summary)
        {
            exchange.ContentId = summary.RequestId;
            e.UserData = exchange;
        }
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

    private async Task RecordGainAsync(CapturedExchange exchange, string method, byte[] original, byte[] forwarded, string rule)
    {
        exchange.GainRule = rule;
        exchange.GainMethod = method;
        exchange.GainLayer = rule is GainLabels.Rules.Unchanged or GainLabels.Rules.CcrPlanFailed ? GainLabels.Unknown : GainLabels.Layers.ToolResult;
        exchange.GainPipeline = compression.Compresses ? GainLabels.Pipelines.Content : GainLabels.Pipelines.Passthrough;
        try
        {
            await gain.RecordAsync(new GainHistoryEntry(exchange.GainId, time.GetUtcNow(), exchange.GainProject,
                    exchange.Dialect.ToString().ToLowerInvariant(), method, rule, exchange.GainLayer, exchange.GainPipeline,
                    GainReportBuilder.RequestTotals(Encoding.UTF8.GetCharCount(original), Encoding.UTF8.GetCharCount(forwarded), original.Length, forwarded.Length)),
                exchange.GainFailures, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.GainWriteFailed(logger, exchange.GainId, exception);
        }
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

        [LoggerMessage(EventId = 1405, Level = LogLevel.Error, Message = "The CCR thread ledger could not be read for a request to {ProviderHost}; the client was told to retry")]
        public static partial void LedgerLookupFailed(ILogger logger, string providerHost, Exception exception);
    }
}