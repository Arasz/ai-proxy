using AiProxy.Hosting.Integration;

namespace AiProxy.Tests.Unit;

public sealed class WindowsProxyOwnershipTests
{
    [Theory]
    [InlineData(1, null, true)]
    [InlineData(0, null, false)]
    [InlineData(null, null, false)]
    [InlineData(1, "https://proxy-config.example/proxy.pac", false)]
    public void Only_enabled_manual_proxy_settings_can_be_restored(int? proxyEnable, string? autoConfigUrl, bool expected)
    {
        Assert.Equal(expected, TitaniumSystemProxySnapshotProvider.WindowsProxyIsActive(proxyEnable, autoConfigUrl));
    }

    [Theory]
    [InlineData("http=127.0.0.1:8080;https=127.0.0.1:8080", "localhost;127.0.0.1;::1", "http=corp.example:80;https=secure.example:443", "", "localhost;127.0.0.1;::1", true)]
    [InlineData("http=127.0.0.1:8080;https=secure.example:443", "localhost;127.0.0.1;::1", "http=corp.example:80;https=secure.example:443", "", "localhost;127.0.0.1;::1", true)]
    [InlineData("http=127.0.0.1:8080;https=secure.example:443", "corp.example", "http=corp.example:80;https=secure.example:443", "corp.example", "corp.example;localhost;127.0.0.1;::1", true)]
    [InlineData("http=127.0.0.1:8080;https=192.168.1.4:8080", "localhost;127.0.0.1;::1", "http=corp.example:80;https=secure.example:443", "", "localhost;127.0.0.1;::1", false)]
    [InlineData("http=192.168.1.4:8080;https=192.168.1.4:8080", "localhost;127.0.0.1;::1", "http=corp.example:80;https=secure.example:443", "", "localhost;127.0.0.1;::1", false)]
    [InlineData("http=127.0.0.1:8080;https=127.0.0.1:8080", "localhost;127.0.0.1;::1;corp.example", "http=corp.example:80;https=secure.example:443", "", "localhost;127.0.0.1;::1", false)]
    [InlineData("http=127.0.0.1:8080;https=127.0.0.1:8080", "localhost;127.0.0.1", "http=corp.example:80;https=secure.example:443", "", "localhost;127.0.0.1;::1", false)]
    public void Ownership_requires_exact_proxy_endpoint_and_bypass_list(
        string proxyServer,
        string proxyOverride,
        string originalProxyServer,
        string originalProxyOverride,
        string expectedProxyOverride,
        bool expected)
    {
        Assert.Equal(expected, TitaniumSystemProxySnapshotProvider.WindowsProxyIsOwned(
            1,
            null,
            proxyServer,
            proxyOverride,
            originalProxyServer,
            originalProxyOverride,
            "127.0.0.1:8080",
            expectedProxyOverride));
    }

}
