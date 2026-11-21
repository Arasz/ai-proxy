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

        [Theory]
    [InlineData("Proxy:Ccr:KeepAliveInterval", "00:00:00")]
    [InlineData("Proxy:Ccr:KeepAliveInterval", "-00:00:00.001")]
    [InlineData("Proxy:Compression:Minimum", "0")]
    [InlineData("Proxy:Compression:Minimum", "-1")]
    public void Out_of_range_value_fails_naming_the_key(string key, string value)
    {
        var failure = Assert.ThrowsAny<Exception>(() => ProxySettings.Load(Configure(key, value)));
        Assert.Contains(key, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Proxy:Ccr:KeepAliveInterval", "00:00:00.001")]
    [InlineData("Proxy:Compression:Minimum", "1")]
    public void Lowest_valid_value_loads(string key, string value)
    {
        ProxySettings.Load(Configure(key, value));
    }

    [Fact]
    public void Every_bound_key_is_in_the_settings_reference()
    {
        var reference = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "reference", "environment-variables.md"));
        var missing = BoundKeys(typeof(ProxySettings), "PROXY").Where(key => !reference.Contains(key, StringComparison.Ordinal)).ToArray();
        Assert.Empty(missing);
    }

    private static IEnumerable<string> BoundKeys(Type settings, string prefix)
    {
        foreach (var property in settings.GetProperties())
        {
            var key = $"{prefix}__{property.Name.ToUpperInvariant()}";
            if (property.PropertyType.Namespace == typeof(ProxySettings).Namespace)
            {
                foreach (var nested in BoundKeys(property.PropertyType, key))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return key;
            }
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "VERSION")))
        {
            directory = directory.Parent ?? throw new InvalidOperationException("The repository root was not found.");
        }

        return directory.FullName;
    }

    private static IConfigurationRoot Configure(string key, string value) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

private static IConfigurationRoot Configure(string key) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = "1" }).Build();
}
