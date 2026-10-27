using System.Diagnostics;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AiProxy.Core.Capture;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Content.Ccr.Loop;
using AiProxy.Core.Gain;
using AiProxy.Hosting;
using Microsoft.Net.Http.Headers;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace AiProxy.Ccr;

/// <summary>
///     Sends a CCR request upstream itself and answers the client with one response: it passes text
///     through as it streams, answers <c>proxy_retrieve</c> calls with follow-ups, and releases the rest.
/// </summary>
/// <param name="store">Where originals not in the request and the thread ledger are read and written.</param>
/// <param name="retrievers">Makes a retriever for one answer.</param>
/// <param name="options">The CCR numbers.</param>
/// <param name="logger">Where each retrieval and failure is logged.</param>
public sealed partial class CcrRelay(IOriginalStore store, Func<CcrRetriever> retrievers, CcrOptions options, ILogger<CcrRelay> logger, IGainStore gain, TimeProvider time) : IDisposable
{
    /// <summary>The header naming the id the original request is kept under.</summary>
    public const string ContentIdHeader = "x-ai-proxy-content-id";

    private const string EventStream = "text/event-stream";
    private const string Chunked = "chunked";

    /// <summary>How often the client gets a <c>ping</c> while a follow-up has not answered yet.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        HeaderNames.Host, HeaderNames.ContentLength, HeaderNames.TransferEncoding, HeaderNames.Connection, HeaderNames.KeepAlive,
        HeaderNames.ProxyConnection, HeaderNames.ProxyAuthorization, HeaderNames.ProxyAuthenticate, HeaderNames.TE, HeaderNames.Trailer,
        HeaderNames.Upgrade, HeaderNames.Expect, HeaderNames.AcceptEncoding, HeaderNames.ContentEncoding
    };

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
    }

    /// <summary>Sends the request upstream and makes the session answer the client from the proxy.</summary>
    /// <param name="e">The Titanium session of the client request.</param>
    /// <param name="exchange">What the inspector keeps about the exchange; its capture buffer gets the bytes the client gets.</param>
    /// <param name="body">The request body as the proxy forwards it.</param>
    /// <param name="plan">The CCR plan of the request.</param>
    /// <param name="originals">The originals of the previews in this request, by id.</param>
    public async Task RelayAsync(SessionEventArgs e, CapturedExchange exchange, byte[] body, CcrPlan plan, IReadOnlyDictionary<string, string> originals)
    {
        var request = e.HttpClient.Request;
        var headers = request.Headers.Where(static header => !HopByHop.Contains(header.Name) && !header.Name.Equals(GainApi.ProjectHeader, StringComparison.OrdinalIgnoreCase))
            .Select(static header => new ForwardedHeader(header.Name, header.Value)).ToArray();
        exchange.Relayed = true;
        if (exchange.CaptureId is not null)
        {
            exchange.Response = ResponseBuffer.For(EventStream, null);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(Message(request.RequestUri, headers, body), HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            Log.UpstreamUnreachable(logger, request.RequestUri.Host, exception);
            var error = Encoding.UTF8.GetBytes(SseEvent.Error(MessagesApi.Errors.Api, Messages.UpstreamUnreachable).Data);
            e.GenericResponse(error, HttpStatusCode.BadGateway, [new HttpHeader(HeaderNames.ContentType, MediaTypeNames.Application.Json)], true);
            return;
        }

        var answer = ClientResponse(response, exchange.ContentId);
        var streams = response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == EventStream;
        var conversation = new Conversation(new RelayServices(store, retrievers, options, logger, _http, gain, time), exchange, headers, request.RequestUri, body, plan, originals);
        e.RespondStreaming(answer, streams ? conversation.RunAsync(response) : conversation.CopyAsync(response), true);
    }

    private static HttpRequestMessage Message(Uri uri, IEnumerable<ForwardedHeader> headers, byte[] body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
        foreach (var header in headers)
        {
            if (!message.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                message.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        return message;
    }

    private static Response ClientResponse(HttpResponseMessage upstream, string? contentId)
    {
        var response = new Response
        {
            StatusCode = (int)upstream.StatusCode,
            StatusDescription = upstream.ReasonPhrase ?? upstream.StatusCode.ToString()
        };
        foreach (var (name, values) in upstream.Headers.Concat(upstream.Content.Headers))
        {
            if (HopByHop.Contains(name))
            {
                continue;
            }

            foreach (var value in values)
            {
                response.Headers.AddHeader(name, value);
            }
        }

        response.Headers.AddHeader(HeaderNames.TransferEncoding, Chunked);
        if (contentId is not null)
        {
            response.Headers.AddHeader(ContentIdHeader, contentId);
        }

        return response;
    }

    /// <summary>A client request header the relay sends upstream.</summary>
    private sealed record ForwardedHeader(string Name, string Value);

    /// <summary>What a conversation reads and writes through.</summary>
    private sealed record RelayServices(IOriginalStore Store, Func<CcrRetriever> Retrievers, CcrOptions Options, ILogger Logger, HttpClient Http, IGainStore Gain, TimeProvider Time);

    /// <summary>One client response across its upstream rounds.</summary>
    private sealed class Conversation(
        RelayServices relay,
        CapturedExchange exchange,
        ForwardedHeader[] headers,
        Uri uri,
        byte[] body,
        CcrPlan plan,
        IReadOnlyDictionary<string, string> originals)
    {
        private CancellationToken _cancellation;
        private Stream _client = Stream.Null;

        public Func<Stream, CancellationToken, Task> CopyAsync(HttpResponseMessage response)
        {
            return async (client, cancellationToken) =>
            {
                using (response)
                {
                    await using var upstream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var buffer = new byte[16 * 1024];
                    int read;
                    while ((read = await upstream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        exchange.Response?.Append(buffer.AsSpan(0, read));
                        await client.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }
            };
        }

        public Func<Stream, CancellationToken, Task> RunAsync(HttpResponseMessage first)
        {
            return async (client, cancellationToken) =>
            {
                _client = client;
                _cancellation = cancellationToken;
                try
                {
                    await LoopAsync(first);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Log.LoopFailed(relay.Logger, uri.Host, exception);
                    await WriteAsync(SseEvent.Error(MessagesApi.Errors.Api, Messages.ResponseLost));
                }
            };
        }

        private async Task LoopAsync(HttpResponseMessage first)
        {
            var loop = new RetrievalLoop();
            var forwarded = body;
            var response = first;
            while (true)
            {
                await ReadRoundAsync(loop, response);
                switch (loop.Decide())
                {
                    case RetrievalStep.Fail:
                        await FailAsync(loop);
                        return;
                    case RetrievalStep.Release:
                        await ReleaseAsync(loop);
                        return;
                }

                if (plan.Mode == CcrMode.Stateless && loop.Round.Lossy)
                {
                    await WriteAsync(loop.Failure(Messages.LossyMessage));
                    return;
                }

                forwarded = await FollowUpAsync(loop, forwarded);
                response = await SendWithPingsAsync(forwarded);
                if (response.IsSuccessStatusCode)
                {
                    continue;
                }

                await RefuseFollowUpAsync(loop, response);
                return;
            }
        }

        private async Task ReadRoundAsync(RetrievalLoop loop, HttpResponseMessage response)
        {
            var started = Stopwatch.GetTimestamp();
            using (response)
            {
                await using var upstream = await response.Content.ReadAsStreamAsync(_cancellation);
                await foreach (var sse in new SseReader().ReadAsync(upstream, _cancellation).ConfigureAwait(false))
                {
                    foreach (var passed in loop.Round.Accept(sse))
                    {
                        await WriteAsync(passed);
                    }
                }
            }

            exchange.Stages.Add(Labels.RoundPrefix + (loop.Answered + 1), Stopwatch.GetElapsedTime(started));
        }

        private async Task FailAsync(RetrievalLoop loop)
        {
            if (loop.Round.Failed)
            {
                return;
            }

            if (!loop.Round.Ended)
            {
                Log.StreamCutOff(relay.Logger, uri.Host, loop.Answered + 1);
                await WriteAsync(loop.Failure(Messages.StreamCutOff));
                return;
            }

            Log.LimitExceeded(relay.Logger, uri.Host, loop.Answered);
            await WriteAsync(loop.Failure(Messages.LimitExceeded));
        }

        private async Task ReleaseAsync(RetrievalLoop loop)
        {
            if (!await LinkAsync(loop).ConfigureAwait(false))
            {
                await WriteAsync(loop.Failure(Messages.ThreadNotRecorded));
                return;
            }

            foreach (var released in loop.Finish())
            {
                await WriteAsync(released);
            }
        }

        private async Task<byte[]> FollowUpAsync(RetrievalLoop loop, byte[] forwarded)
        {
            var results = loop.ToolResults(await AnswerAsync(loop).ConfigureAwait(false));
            var followUp = plan.Mode == CcrMode.Thread
                ? RetrievalFollowUp.Thread(forwarded, loop.Round.MessageId, results)
                : RetrievalFollowUp.Stateless(forwarded, loop.Round.Content(), results);
            loop.NextRound();
            return followUp;
        }

        private async Task RefuseFollowUpAsync(RetrievalLoop loop, HttpResponseMessage response)
        {
            using (response)
            {
                var error = await response.Content.ReadAsStringAsync(_cancellation);
                Log.FollowUpRefused(relay.Logger, uri.Host, (int)response.StatusCode, error);
                await WriteAsync(IsJson(error)
                    ? new SseEvent(MessagesApi.Events.Error, error)
                    : SseEvent.Error(MessagesApi.Errors.Api, string.Format(Messages.FollowUpRefused, (int)response.StatusCode)));
                loop.Failure(Messages.FollowUpRefusedTrigger);
            }
        }

        private async Task<bool> LinkAsync(RetrievalLoop loop)
        {
            if (plan.Mode != CcrMode.Thread || loop.FirstMessageId.Length == 0 || loop.Round.MessageId.Length == 0)
            {
                return true;
            }

            try
            {
                await relay.Store.LinkThreadAsync(loop.FirstMessageId, loop.Round.MessageId, _cancellation);
                return true;
            }
            catch (Exception exception) when (!_cancellation.IsCancellationRequested)
            {
                Log.LedgerWriteFailed(relay.Logger, loop.FirstMessageId, exception);
                return false;
            }
        }

        private async Task<IReadOnlyDictionary<string, CcrRetrieval>> AnswerAsync(RetrievalLoop loop)
        {
            var answers = new Dictionary<string, CcrRetrieval>(StringComparer.Ordinal);
            foreach (var call in loop.Round.ToolCalls.Where(static call => call.IsRetrieval))
            {
                var answer = loop.AtLimit ? CcrRetrieval.Limit() : relay.Retrievers().Answer(call.Input, await OriginalAsync(call.Input).ConfigureAwait(false), relay.Options);
                answers[call.Id] = answer;
                Log.Retrieved(relay.Logger, call.Input, answer.Error ?? Labels.Success, answer.Text.Length, loop.Answered);
                await RecordRecallAsync(call.Input, answer);
            }

            return answers;
        }

        private async Task RecordRecallAsync(string input, CcrRetrieval answer)
        {
            try
            {
                var id = "";
                var mode = GainLabels.Unknown;
                try
                {
                    using var arguments = JsonDocument.Parse(input);
                    if (arguments.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (arguments.RootElement.TryGetProperty("id", out var identifier) && identifier.ValueKind == JsonValueKind.String)
                        {
                            id = identifier.GetString() ?? "";
                        }

                        if (arguments.RootElement.TryGetProperty("mode", out var selected) && selected.ValueKind == JsonValueKind.String)
                        {
                            mode = selected.GetString() ?? GainLabels.Unknown;
                        }
                    }
                }
                catch (JsonException)
                {
                }

                await relay.Gain.RecallAsync(new GainRecall(relay.Time.GetUtcNow(), id, exchange.GainProject,
                        exchange.GainProject is null ? GainLabels.Unknown : GainLabels.Attributions.Caller, mode, answer.Text.Length,
                        GainReportBuilder.Tokens(answer.Text.Length), answer.Error), exchange.Dialect.ToString().ToLowerInvariant(),
                    exchange.GainMethod, exchange.GainRule, exchange.GainLayer, exchange.GainPipeline, _cancellation);
            }
            catch (Exception exception) when (!_cancellation.IsCancellationRequested)
            {
                Log.RecallWriteFailed(relay.Logger, exchange.GainId, exception);
            }
        }

        private async Task<string?> OriginalAsync(string input)
        {
            string? id;
            try
            {
                using var arguments = JsonDocument.Parse(input);
                id = arguments.RootElement.ValueKind == JsonValueKind.Object && arguments.RootElement.TryGetProperty(MessagesApi.Fields.Id, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }

            if (id is null)
            {
                return null;
            }

            if (originals.TryGetValue(id, out var original))
            {
                return original;
            }

            try
            {
                return await relay.Store.FindAsync(id, _cancellation);
            }
            catch (Exception exception) when (!_cancellation.IsCancellationRequested)
            {
                Log.OriginalLookupFailed(relay.Logger, id, exception);
                return null;
            }
        }

        private static bool IsJson(string text)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private async Task<HttpResponseMessage> SendWithPingsAsync(byte[] followUp)
        {
            var sending = relay.Http.SendAsync(Message(uri, headers, followUp), HttpCompletionOption.ResponseHeadersRead, _cancellation);
            while (await Task.WhenAny(sending, Task.Delay(PingInterval, _cancellation)).ConfigureAwait(false) != sending)
            {
                await WriteAsync(SseEvent.Ping);
            }

            return await sending;
        }

        private async Task WriteAsync(SseEvent sse)
        {
            var bytes = sse.ToBytes();
            exchange.Response?.Append(bytes);
            await _client.WriteAsync(bytes, _cancellation);
            await _client.FlushAsync(_cancellation);
        }
    }

    private static class Messages
    {
        public const string UpstreamUnreachable = "ai-proxy could not reach the provider.";
        public const string ResponseLost = "ai-proxy lost the provider response.";
        public const string StreamCutOff = "The provider stream ended before the message did; retry the turn.";
        public const string LimitExceeded = "The model kept calling proxy_retrieve after its limit; retry the turn.";
        public const string ThreadNotRecorded = "ai-proxy could not record the thread; retry the turn.";
        public const string LossyMessage = "ai-proxy could not rebuild the assistant message for a retrieval; retry the turn.";
        public const string FollowUpRefused = "The provider refused a retrieval follow-up with {0}.";
        public const string FollowUpRefusedTrigger = "follow-up refused";
    }

    private static class Labels
    {
        public const string RoundPrefix = "ccr.round-";
        public const string Success = "ok";
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1609, Level = LogLevel.Warning, Message = "Recall metadata for request {RequestId} could not be recorded")]
        public static partial void RecallWriteFailed(ILogger logger, string requestId, Exception exception);

        [LoggerMessage(EventId = 1601, Level = LogLevel.Information, Message = "proxy_retrieve {Arguments} answered {Outcome}, {Characters} characters, round {Round}")]
        public static partial void Retrieved(ILogger logger, string arguments, string outcome, int characters, int round);

        [LoggerMessage(EventId = 1602, Level = LogLevel.Warning, Message = "The provider {ProviderHost} could not be reached for a CCR request")]
        public static partial void UpstreamUnreachable(ILogger logger, string providerHost, Exception exception);

        [LoggerMessage(EventId = 1603, Level = LogLevel.Warning, Message = "The CCR loop with {ProviderHost} failed; the client got an error event")]
        public static partial void LoopFailed(ILogger logger, string providerHost, Exception exception);

        [LoggerMessage(EventId = 1604, Level = LogLevel.Warning, Message = "The model called proxy_retrieve after {Rounds} rounds with {ProviderHost}; the client got an error event")]
        public static partial void LimitExceeded(ILogger logger, string providerHost, int rounds);

        [LoggerMessage(EventId = 1605, Level = LogLevel.Error, Message = "The thread ledger could not record message {MessageId}; the client got an error event instead of the message")]
        public static partial void LedgerWriteFailed(ILogger logger, string messageId, Exception exception);

        [LoggerMessage(EventId = 1606, Level = LogLevel.Warning, Message = "The original {Id} could not be read; the model was told it is not kept")]
        public static partial void OriginalLookupFailed(ILogger logger, string id, Exception exception);

        [LoggerMessage(EventId = 1607, Level = LogLevel.Warning, Message = "{ProviderHost} refused a retrieval follow-up with {StatusCode}: {Error}")]
        public static partial void FollowUpRefused(ILogger logger, string providerHost, int statusCode, string error);

        [LoggerMessage(EventId = 1608, Level = LogLevel.Warning, Message = "The stream from {ProviderHost} ended before message_stop in round {Round}; the client got an error event")]
        public static partial void StreamCutOff(ILogger logger, string providerHost, int round);
    }
}