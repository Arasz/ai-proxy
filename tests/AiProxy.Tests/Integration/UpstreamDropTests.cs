using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AiProxy.Ccr;
using AiProxy.Core.Capture;
using AiProxy.Core.Compression;
using AiProxy.Core.Providers;
using AiProxy.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests.Integration;

/// <summary>What a client sees when the upstream drops its connection partway through a response.</summary>
public sealed class UpstreamDropTests
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(20);
    private static readonly byte[] FirstEvent = "event: message_start\ndata: {\"type\":\"message_start\"}\n\n"u8.ToArray();

    [Theory(Skip = TimingDependent.Reason)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task A_stream_the_upstream_drops_between_chunks_fails_at_the_client_without_waiting_for_its_timeout(bool provider, bool capture)
    {
        var firstEventRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = async (_, context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.Body.WriteAsync(FirstEvent);
            await context.Response.Body.FlushAsync();
            await firstEventRead.Task.WaitAsync(ClientTimeout);
            context.Abort();
        };
        var logs = new RecordingLoggerProvider();
        await using var proxy = await StartProxyAsync(provider, capture, logs);
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true }) { Timeout = ClientTimeout };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(upstream.BaseAddress, "/v1/messages")) { Content = Json() };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var head = new byte[FirstEvent.Length];
        await body.ReadExactlyAsync(head, TestContext.Current.CancellationToken);
        // HttpClient.Timeout stops at the headers, so the body read needs its own deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(ClientTimeout);
        var elapsed = Stopwatch.StartNew();
        firstEventRead.SetResult();
        var failure = await Record.ExceptionAsync(() => body.CopyToAsync(Stream.Null, deadline.Token));
        elapsed.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FirstEvent, head);
        Assert.Equal(HttpRequestError.ResponseEnded, Assert.IsType<HttpIOException>(failure).HttpRequestError);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"the client waited {elapsed.Elapsed.TotalSeconds:F1} s for a stream the upstream had dropped");
        var warning = Assert.Single(logs.Entries, entry => entry.EventId == 1306);
        Assert.Equal((typeof(TitaniumProxyService).FullName, LogLevel.Warning), (warning.Category, warning.Level));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stream_that_ends_cleanly_keeps_the_client_connection_for_the_next_request(bool provider)
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = static async (_, context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.Body.WriteAsync(FirstEvent);
            await context.Response.Body.FlushAsync();
        };
        await using var proxy = await StartProxyAsync(provider, false);
        var connections = 0;
        using var client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxy.ProxyAddress),
            UseProxy = true,
            ConnectCallback = async (context, cancellationToken) =>
            {
                Interlocked.Increment(ref connections);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                return new NetworkStream(socket, true);
            }
        }) { Timeout = ClientTimeout };

        Assert.Equal(FirstEvent, await SendAsync(client, upstream.BaseAddress));
        Assert.Equal(FirstEvent, await SendAsync(client, upstream.BaseAddress));

        Assert.Equal(1, Volatile.Read(ref connections));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_body_the_upstream_ends_by_closing_reaches_the_client_whole_and_logs_no_drop(bool provider)
    {
        const string body = "a body delimited by the close";
        await using var upstream = await CloseDelimitedUpstream.StartAsync(body);
        var logs = new RecordingLoggerProvider();
        await using var proxy = await StartProxyAsync(provider, false, logs);
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true }) { Timeout = ClientTimeout };

        Assert.Equal(body, Encoding.UTF8.GetString(await SendAsync(client, upstream.BaseAddress)));
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId == 1306);
    }

    [Fact]
    public async Task Unreadable_upstream_state_is_logged_once_and_responses_still_reach_the_client()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        var logs = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var directory = new TemporaryDirectory();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var service = new TitaniumProxyService(
            null,
            new ProxyRuntimeOptions(IPAddress.Loopback, port, false, false, directory.Path),
            new ProviderRequestInspector(new PassThroughCompressionPipeline(), new CanonicalProviderHosts(), new NoRequestCapture(), new CcrOff(), NullLogger<ProviderRequestInspector>.Instance, TimeProvider.System, new NoGainLedger()),
            new CanonicalProviderHosts(),
            new ProcessSystemProxyGuardLauncher(),
            loggerFactory,
            TimeSpan.Zero,
            static _ => throw new MissingMethodException("renamed"));
        await service.StartAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy(service.Address), UseProxy = true }) { Timeout = ClientTimeout };

        Assert.Equal("upstream ok", Encoding.UTF8.GetString(await SendAsync(client, upstream.BaseAddress)));
        Assert.Equal("upstream ok", Encoding.UTF8.GetString(await SendAsync(client, upstream.BaseAddress)));

        await service.StopAsync(CancellationToken.None);
        var error = Assert.Single(logs.Entries, entry => entry.EventId == 1307);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.IsType<MissingMethodException>(error.Exception);
    }

    private static Task<ProxyHost> StartProxyAsync(bool provider, bool capture, ILoggerProvider? logs = null)
    {
        return ProxyHost.StartAsync(
            capture ? new Dictionary<string, string> { ["Proxy:Capture"] = "true" } : new Dictionary<string, string>(),
            providerHosts: provider ? new LoopbackProviderHosts(Dialect.Anthropic) : new NoProviderHosts(),
            logging: logs);
    }

    private static StringContent Json()
    {
        return new StringContent("""{"model":"m","stream":true}""", Encoding.UTF8, "application/json");
    }

    private static async Task<byte[]> SendAsync(HttpClient client, Uri upstream)
    {
        using var content = Json();
        using var response = await client.PostAsync(new Uri(upstream, "/v1/messages"), content, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
    }

    private sealed class NoProviderHosts : IProviderHosts
    {
        public Dialect? Find(string host)
        {
            return null;
        }
    }

    /// <summary>An upstream that answers with neither a length nor chunking, so its close ends the body.</summary>
    private sealed class CloseDelimitedUpstream : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serving;

        private CloseDelimitedUpstream(TcpListener listener, string body)
        {
            _listener = listener;
            _serving = ServeAsync(body);
        }

        public Uri BaseAddress => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await Record.ExceptionAsync(() => _serving);
        }

        public static Task<CloseDelimitedUpstream> StartAsync(string body)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new CloseDelimitedUpstream(listener, body));
        }

        private async Task ServeAsync(string body)
        {
            using var socket = await _listener.AcceptSocketAsync();
            await using var stream = new NetworkStream(socket);
            await ReadRequestAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n{body}"));
            socket.Shutdown(SocketShutdown.Both);
        }

        /// <summary>Reads the headers and the Content-Length body, so closing never resets unread request bytes.</summary>
        private static async Task ReadRequestAsync(NetworkStream stream)
        {
            var received = new List<byte>();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    return;
                }

                received.AddRange(buffer.AsSpan(0, read));
                var text = Encoding.ASCII.GetString(received.ToArray());
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }

                var length = text[..end].Split("\r\n")
                    .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    .Select(line => int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture))
                    .FirstOrDefault();
                if (received.Count >= end + 4 + length)
                {
                    return;
                }
            }
        }
    }
}