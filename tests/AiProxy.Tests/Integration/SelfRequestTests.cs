using System.Net;
using Microsoft.Extensions.Logging;

namespace AiProxy.Tests.Integration;

public sealed class SelfRequestTests
{
    [Theory]
    [InlineData(false, "127.0.0.1")]
    [InlineData(true, "127.0.0.1")]
    [InlineData(true, "localhost")]
    public async Task A_request_addressed_to_the_proxy_port_gets_a_400_and_one_warning(bool throughProxy, string host)
    {
        var logs = new RecordingLoggerProvider();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), logging: logs);
        using var handler = new SocketsHttpHandler { UseProxy = throughProxy, Proxy = throughProxy ? new WebProxy(proxy.ProxyAddress) : null };
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(new Uri($"http://{host}:{proxy.ProxyAddress.Port}/v1/messages"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("forward proxy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Single(logs.Entries, entry => entry.EventId == 1309 && entry.Level == LogLevel.Warning);
    }
}
