using AiProxy.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Tests.Unit.Cli;

public sealed class ProxySettingsStrictnessTests
{
    [Theory]
    [InlineData("Proxy:Prot")]
    [InlineData("Proxy:Compression:Enabeld")]
    public void Unknown_proxy_key_fails_naming_the_key(string key)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => ProxySettings.Load(Configure(key)));
        Assert.Contains(key.Split(':')[^1], failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_proxy_key_fails_service_registration()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddProxy(Configure("Proxy:Prot")));
    }

    [Fact]
    public void Known_keys_load()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Proxy:Port"] = "8090", ["Proxy:Compression:Enabled"] = "false" }).Build();
        var settings = ProxySettings.Load(configuration);
        Assert.Equal(8090, settings.Port);
        Assert.False(settings.Compression.Enabled);
    }

    private static IConfigurationRoot Configure(string key) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = "1" }).Build();
}
