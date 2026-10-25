using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AiProxy.Core.Capture;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Content.Ccr.Loop;
using AiProxy.Hosting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace AiProxy.Ccr;

/// <summary>
/// Sends a CCR request upstream itself and answers the client with one response: it passes text
/// through as it streams, answers <c>proxy_retrieve</c> calls with follow-ups, and releases the rest.
/// </summary>
/// <param name="store">Where originals not in the request and the thread ledger are read and written.</param>
/// <param name="retrievers">Makes a retriever for one answer.</param>
/// <param name="options">The CCR numbers.</param>
/// <param name="logger">Where each retrieval and failure is logged.</param>
public sealed partial class CcrRelay(IOriginalStore store, Func<CcrRetriever> retrievers, CcrOptions options, ILogger<CcrRelay> logger) : IDisposable
{
    /// <summary>How often the client gets a <c>ping</c> while a follow-up has not answered yet.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authorization",
        "Proxy-Authenticate", "TE", "Trailer", "Upgrade", "Expect", "Accept-Encoding", "Content-Encoding",
    };

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Sends the request upstream and makes the session answer the client from the proxy.</summary>
    /// <param name="e">The Titanium session of the client request.</param>
    /// <param name="exchange">What the inspector keeps about the exchange; its capture buffer gets the bytes the client gets.</param>
    /// <param name="body">The request body as the proxy forwards it.</param>
    /// <param name="plan">The CCR plan of the request.</param>
    /// <param name="originals">The originals of the previews in this request, by id.</param>
    public async Task RelayAsync(SessionEventArgs e, CapturedExchange exchange, byte[] body, CcrPlan plan, IReadOnlyDictionary<string, string> originals)
    {
        var request = e.HttpClient.Request;
        var headers = request.Headers.Where(static header => !HopByHop.Contains(header.Name)).Select(static header => new ForwardedHeader(header.Name, header.Value)).ToArray();
        exchange.Relayed = true;
        exchange.Response = ResponseBuffer.For("text/event-stream", null);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(Message(request.RequestUri, headers, body), HttpCompletionOption.ResponseHeadersRead, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            Log.UpstreamUnreachable(logger, request.RequestUri.Host, exception);
            var error = Encoding.UTF8.GetBytes(SseEvent.Error("api_error", "ai-proxy could not reach the provider.").Data);
            e.GenericResponse(error, HttpStatusCode.BadGateway, [new Titanium.Web.Proxy.Models.HttpHeader("Content-Type", "application/json")], closeServerConnection: true);
            return;
        }

        var answer = ClientResponse(response, exchange.ContentId);
        var streams = response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/event-stream";
        var conversation = new Conversation(new RelayServices(store, retrievers, options, logger, _http), exchange, headers, request.RequestUri, body, plan, originals);
        e.RespondStreaming(answer, streams ? conversation.RunAsync(response) : conversation.CopyAsync(response), closeServerConnection: true);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

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
            StatusDescription = upstream.ReasonPhrase ?? upstream.StatusCode.ToString(),
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

        response.Headers.AddHeader("Transfer-Encoding", "chunked");
        if (contentId is not null)
        {
            response.Headers.AddHeader("x-ai-proxy-content-id", contentId);
        }

        return response;
    }

    /// <summary>A client request header the relay sends upstream.</summary>
    private sealed record ForwardedHeader(string Name, string Value);

    /// <summary>What a conversation reads and writes through.</summary>
    private sealed record RelayServices(IOriginalStore Store, Func<CcrRetriever> Retrievers, CcrOptions Options, ILogger Logger, HttpClient Http);

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
        private Stream _client = Stream.Null;
        private CancellationToken _cancellation;

        public Func<Stream, CancellationToken, Task> CopyAsync(HttpResponseMessage response) => async (client, cancellationToken) =>
        {
            using (response)
            {
                await using var upstream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await upstream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    exchange.Response?.Append(buffer.AsSpan(0, read));
                    await client.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
        };

        public Func<Stream, CancellationToken, Task> RunAsync(HttpResponseMessage first) => async (client, cancellationToken) =>
        {
            _client = client;
            _cancellation = cancellationToken;
            try
            {
                await LoopAsync(first).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException && !cancellationToken.IsCancellationRequested)
            {
                Log.LoopFailed(relay.Logger, uri.Host, exception);
                await WriteAsync(SseEvent.Error("api_error", "ai-proxy lost the provider response.")).ConfigureAwait(false);
            }
        };

        private async Task LoopAsync(HttpResponseMessage first)
        {
            var loop = new RetrievalLoop();
            var forwarded = body;
            var response = first;
            while (true)
            {
                var started = Stopwatch.GetTimestamp();
                using (response)
                {
                    await using var upstream = await response.Content.ReadAsStreamAsync(_cancellation).ConfigureAwait(false);
                    await foreach (var sse in SseReader.ReadAsync(upstream, _cancellation).ConfigureAwait(false))
                    {
                        foreach (var passed in loop.Round.Accept(sse))
                        {
                            await WriteAsync(passed).ConfigureAwait(false);
                        }
                    }
                }

                exchange.Stages.Add($"ccr.round-{loop.Answered + 1}", Stopwatch.GetElapsedTime(started));
                switch (loop.Decide())
                {
                    case RetrievalStep.Fail:
                        if (!loop.Round.Failed)
                        {
                            Log.LimitExceeded(relay.Logger, uri.Host, loop.Answered);
                            await WriteAsync(loop.Failure("The model kept calling proxy_retrieve after its limit; retry the turn.")).ConfigureAwait(false);
                        }

                        return;
                    case RetrievalStep.Release:
                        if (!await LinkAsync(loop).ConfigureAwait(false))
                        {
                            await WriteAsync(loop.Failure("ai-proxy could not record the thread; retry the turn.")).ConfigureAwait(false);
                            return;
                        }

                        foreach (var released in loop.Finish())
                        {
                            await WriteAsync(released).ConfigureAwait(false);
                        }

                        return;
                }

                if (plan.Mode == CcrMode.Stateless && loop.Round.Lossy)
                {
                    await WriteAsync(loop.Failure("ai-proxy could not rebuild the assistant message for a retrieval; retry the turn.")).ConfigureAwait(false);
                    return;
                }

                var results = loop.ToolResults(await AnswerAsync(loop).ConfigureAwait(false));
                forwarded = plan.Mode == CcrMode.Thread
                    ? RetrievalFollowUp.Thread(forwarded, loop.Round.MessageId!, results)
                    : RetrievalFollowUp.Stateless(forwarded, loop.Round.Content(), results);
                loop.NextRound();
                response = await SendWithPingsAsync(forwarded).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    using (response)
                    {
                        var error = await response.Content.ReadAsStringAsync(_cancellation).ConfigureAwait(false);
                        Log.FollowUpRefused(relay.Logger, uri.Host, (int)response.StatusCode, error);
                        await WriteAsync(IsJson(error) ? new SseEvent("error", error) : SseEvent.Error("api_error", $"The provider refused a retrieval follow-up with {(int)response.StatusCode}.")).ConfigureAwait(false);
                        loop.Failure("follow-up refused");
                        return;
                    }
                }
            }
        }

        private async Task<bool> LinkAsync(RetrievalLoop loop)
        {
            if (plan.Mode != CcrMode.Thread || loop.FirstMessageId is not { } first || loop.Round.MessageId is not { } last)
            {
                return true;
            }

            try
            {
                await relay.Store.LinkThreadAsync(first, last, _cancellation).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (!_cancellation.IsCancellationRequested)
            {
                Log.LedgerWriteFailed(relay.Logger, first, exception);
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
                Log.Retrieved(relay.Logger, call.Input, answer.Error ?? "ok", answer.Text.Length, loop.Answered);
            }

            return answers;
        }

        private async Task<string?> OriginalAsync(string input)
        {
            string? id;
            try
            {
                using var arguments = JsonDocument.Parse(input);
                id = arguments.RootElement.ValueKind == JsonValueKind.Object && arguments.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
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
                return await relay.Store.FindAsync(id, _cancellation).ConfigureAwait(false);
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
                await WriteAsync(SseEvent.Ping).ConfigureAwait(false);
            }

            return await sending.ConfigureAwait(false);
        }

        private async Task WriteAsync(SseEvent sse)
        {
            var bytes = sse.ToBytes();
            exchange.Response?.Append(bytes);
            await _client.WriteAsync(bytes, _cancellation).ConfigureAwait(false);
            await _client.FlushAsync(_cancellation).ConfigureAwait(false);
        }
    }

    private static partial class Log
    {
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
    }
}
