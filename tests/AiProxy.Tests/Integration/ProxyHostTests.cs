using System.Net;

namespace AiProxy.Tests.Integration;

public sealed class ProxyHostTests
{
    [Fact]
    public async Task The_test_host_answers_health_with_system_proxy_and_certificate_changes_disabled()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}