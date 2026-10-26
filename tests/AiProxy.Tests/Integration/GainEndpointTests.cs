using System.Net;
using System.Text.Json;

namespace AiProxy.Tests.Integration;

public sealed class GainEndpointTests
{
    [Fact]
    public async Task Gain_is_available_without_raw_capture_or_compression()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        using var response = await client.GetAsync("/gain", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, report.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("estimated", report.RootElement.GetProperty("measurement").GetProperty("kind").GetString());
        Assert.Equal(0, report.RootElement.GetProperty("summary").GetProperty("requests").GetInt64());
    }

    [Theory]
    [InlineData("from=garbage")]
    [InlineData("from=2026-12-02&to=2026-12-01")]
    [InlineData("from=2026-12-01&to=2026-12-01")]
    [InlineData("historyLimit=-1")]
    [InlineData("historyLimit=1001")]
    [InlineData("historyLimit=bad")]
    [InlineData("project=%20")]
    public async Task Invalid_filters_return_problem_details(string filter)
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        using var response = await client.GetAsync($"/gain?{filter}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("evil.test")]
    [InlineData("evil.test:17432")]
    public async Task A_request_named_for_another_host_is_refused_so_a_rebound_page_cannot_read_gain_or_originals(string host)
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/gain");
        request.Headers.Host = host;

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task A_request_named_for_the_loopback_host_is_served(string host)
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/gain");
        request.Headers.Host = $"{host}:{proxy.BaseAddress.Port}";

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
