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

    [Fact]
    public async Task Health_names_the_product_and_version_and_keeps_status()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        using var client = new HttpClient { BaseAddress = proxy.BaseAddress };

        using var json = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/health", TestContext.Current.CancellationToken));

        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("ai-proxy", json.RootElement.GetProperty("product").GetString());
        Assert.Equal(AiProxy.Cli.EntryPoint.ProductVersion, json.RootElement.GetProperty("version").GetString());
    }
}
