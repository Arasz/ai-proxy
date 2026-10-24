using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AiProxy.Core.Providers;
using AiProxy.Hosting;

namespace AiProxy.Tests.Integration;

/// <summary>What a client sees when the upstream drops its connection partway through a response.</summary>
public sealed class UpstreamDropTests
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(20);
    private static readonly byte[] FirstEvent = "event: message_start\ndata: {\"type\":\"message_start\"}\n\n"u8.ToArray();

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task A_stream_the_upstream_drops_between_chunks_fails_at_the_client_without_waiting_for_its_timeout(bool provider, bool capture)
    {
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.Handler = static async (_, context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.Body.WriteAsync(FirstEvent);
            await context.Response.Body.FlushAsync();
            await Task.Delay(100);
            context.Abort();
        };
        await using var proxy = await StartProxyAsync(provider, capture);
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true }) { Timeout = ClientTimeout };

        var elapsed = Stopwatch.StartNew();
        var failure = await Record.ExceptionAsync(() => SendStreamAsync(client, upstream));
        elapsed.Stop();

        Assert.IsType<HttpRequestException>(failure, exactMatch: false);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"the client waited {elapsed.Elapsed.TotalSeconds:F1} s for a stream the upstream dropped after 0.1 s");
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
        await using var proxy = await StartProxyAsync(provider, capture: false);
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
                return new NetworkStream(socket, ownsSocket: true);
            },
        }) { Timeout = ClientTimeout };

        Assert.Equal(FirstEvent, await SendStreamAsync(client, upstream));
        Assert.Equal(FirstEvent, await SendStreamAsync(client, upstream));

        Assert.Equal(1, Volatile.Read(ref connections));
    }

    private static Task<ProxyHost> StartProxyAsync(bool provider, bool capture) =>
        ProxyHost.StartAsync(
            capture ? new Dictionary<string, string> { ["PROXY_CAPTURE"] = "on" } : new Dictionary<string, string>(),
            providerHosts: provider ? new LoopbackProviderHosts(Dialect.Anthropic) : new NoProviderHosts());

    private static async Task<byte[]> SendStreamAsync(HttpClient client, FakeUpstream upstream)
    {
        using var content = new StringContent("""{"model":"m","stream":true}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri(upstream.BaseAddress, "/v1/messages"), content, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
    }

    private sealed class NoProviderHosts : IProviderHosts
    {
        public Dialect? Find(string host) => null;
    }
}
