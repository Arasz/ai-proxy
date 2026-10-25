using System.Net;
using System.Net.Sockets;
using AiProxy.Core.Compression;
using AiProxy.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests.Integration;

public sealed class ForwardProxyTests
{
    [Fact]
    public async Task A_listener_started_before_proxy_binding_cannot_claim_the_proxy_port()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        TcpListener? competingListener = null;
        try
        {
            await using var proxy = await TestProxy.StartAsync(port =>
            {
                competingListener = new TcpListener(IPAddress.Loopback, port);
                competingListener.Start();
            });
            using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.Address), UseProxy = true };
            using var client = new HttpClient(handler);

            using var response = await client.GetAsync(new Uri(upstream.BaseAddress, "/competing-startup"), TestContext.Current.CancellationToken);

            Assert.InRange(proxy.Address.Port, 1, 65535);
            Assert.NotEqual(((IPEndPoint)competingListener!.LocalEndpoint).Port, proxy.Address.Port);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("upstream ok", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("/competing-startup", Assert.Single(upstream.Requests).Path);
        }
        finally
        {
            competingListener?.Stop();
        }
    }

    [Fact]
    public async Task An_unknown_http_destination_passes_through_with_its_request_and_response()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await TestProxy.StartAsync();
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.Address), UseProxy = true };
        using var client = new HttpClient(handler);
        const string jsonRpc = "{\"jsonrpc\":\"2.0\",\"method\":\"tools/list\"}";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(upstream.BaseAddress, "/mcp?mode=tools"))
        {
            Content = new StringContent(jsonRpc),
        };
        request.Headers.TryAddWithoutValidation("x-mcp-client", "agent");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("upstream ok", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var received = Assert.Single(upstream.Requests);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/mcp", received.Path);
        Assert.Equal("?mode=tools", received.QueryString);
        Assert.Equal(jsonRpc, received.Body);
        Assert.Equal("agent", received.Headers["x-mcp-client"]);
        Assert.False(received.Headers.ContainsKey("via"));
    }

    private sealed class TestProxy(TitaniumProxyService service, TemporaryDirectory directory) : IAsyncDisposable
    {
        public Uri Address => new($"http://127.0.0.1:{service.EndPoints.Single().Port}");

        public static async Task<TestProxy> StartAsync(Action<int>? beforeBind = null)
        {
            beforeBind?.Invoke(0);
            var directory = new TemporaryDirectory();
            var service = new TitaniumProxyService(
                new ProxyRuntimeOptions(IPAddress.Loopback, 0, ConfigureSystemProxy: false, TrustRootCertificate: false, directory.Path),
                new ProviderRequestInspector(new PassThroughCompressionPipeline(), new CanonicalProviderHosts(), compressionEnabled: false, NullLogger<ProviderRequestInspector>.Instance),
                new CanonicalProviderHosts(),
                NullLoggerFactory.Instance);
            try
            {
                await service.StartAsync(TestContext.Current.CancellationToken);
                return new TestProxy(service, directory);
            }
            catch
            {
                try
                {
                    await service.DisposeAsync();
                }
                finally
                {
                    directory.Dispose();
                }

                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await service.DisposeAsync();
            }
            finally
            {
                directory.Dispose();
            }
        }
    }
}
