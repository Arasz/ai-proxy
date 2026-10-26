using System.Net;
using System.Net.Http.Json;
using System.Text;
using AiProxy.Core.Gain;
using AiProxy.Core.Providers;
using AiProxy.Hosting;

namespace AiProxy.Tests.Integration;

public sealed class GainTrafficTests
{
    [Fact]
    public async Task Unchanged_provider_traffic_is_counted_without_capture_and_private_project_never_leaves_proxy()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new LoopbackProviderHosts(Dialect.Anthropic));
        using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.ProxyAddress), UseProxy = true };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(upstream.BaseAddress, "/v1/messages"))
        {
            Content = new StringContent("{\"messages\":[]}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Ai-Proxy-Project", "/project/a");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var api = new HttpClient { BaseAddress = proxy.BaseAddress };
        var report = await api.GetFromJsonAsync<GainReport>("/gain?project=%2Fproject%2Fa", TestContext.Current.CancellationToken);
        Assert.Equal(1, report!.Summary.Requests);
        Assert.Equal(0, report.Summary.EstimatedSavedTokens);
        Assert.Equal("/project/a", Assert.Single(report.History).Project);
        Assert.DoesNotContain(Assert.Single(upstream.Requests).Headers.Keys, key => key.Equals("X-Ai-Proxy-Project", StringComparison.OrdinalIgnoreCase));
    }
}
