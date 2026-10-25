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
}
