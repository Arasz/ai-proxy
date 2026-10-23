using System.Net;
using AiProxy.Core.Compression;
using AiProxy.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests.Integration;

public class ForwardProxyTests
{
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

    private sealed class TestProxy : IAsyncDisposable
    {
        private readonly TitaniumProxyService _service;

        private TestProxy(TitaniumProxyService service)
        {
            _service = service;
        }

        public Uri Address => _service.Address;

        public static async Task<TestProxy> StartAsync()
        {
            var portListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            portListener.Start();
            var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
            portListener.Stop();
            var service = new TitaniumProxyService(
                new ProxyRuntimeOptions(IPAddress.Loopback, port, ConfigureSystemProxy: false, TrustRootCertificate: false),
                new ProviderRequestInspector(new PassThroughCompressionPipeline()),
                NullLogger<TitaniumProxyService>.Instance);
            await service.StartAsync(TestContext.Current.CancellationToken);
            return new TestProxy(service);
        }

        public async ValueTask DisposeAsync()
        {
            await _service.StopAsync(CancellationToken.None);
            await _service.DisposeAsync();
        }
    }
}
