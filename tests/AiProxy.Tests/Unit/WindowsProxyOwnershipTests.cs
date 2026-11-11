using AiProxy.Hosting.Integration;
using Titanium.Web.Proxy.Models;

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
            new WindowsProxyInfo(1, null, proxyServer, proxyOverride),
            new WindowsProxyInfo(1, null, originalProxyServer, originalProxyOverride),
            "127.0.0.1:8080",
            expectedProxyOverride));
    }

    [Theory]
    [InlineData("http=127.0.0.1:8080;https=127.0.0.1:8080", "http=corp.example:80;https=secure.example:443;socks=corp.example:1080", true)]
    [InlineData("http=127.0.0.1:8080;https=127.0.0.1:8080;socks=corp.example:1080", "http=corp.example:80;https=secure.example:443;socks=corp.example:1080", true)]
    [InlineData("http=127.0.0.1:8080;https=127.0.0.1:8080;socks=other.example:1080", "http=corp.example:80;https=secure.example:443;socks=corp.example:1080", false)]
    [InlineData("http=127.0.0.1:8080;https=127.0.0.1:8080;ftp=other.example:21", "http=corp.example:80;https=secure.example:443", false)]
    public void Ownership_refuses_a_non_http_proxy_entry_changed_since_the_snapshot(string proxyServer, string originalProxyServer, bool expected)
    {
        Assert.Equal(expected, TitaniumSystemProxySnapshotProvider.WindowsProxyIsOwned(
            new WindowsProxyInfo(1, null, proxyServer, "localhost;127.0.0.1;::1"),
            new WindowsProxyInfo(1, null, originalProxyServer, ""),
            "127.0.0.1:8080",
            "localhost;127.0.0.1;::1"));
    }

    [Theory]
    [InlineData(1, "http=corp.example:80;https=secure.example:443", 1, true)]
    [InlineData(0, "http=127.0.0.1:8080;https=127.0.0.1:8080", 0, true)]
    [InlineData(0, "http=127.0.0.1:8080;https=127.0.0.1:8080", 1, false)]
    [InlineData(1, "http=other.example:80;https=other.example:443", 1, false)]
    public void A_partly_restored_snapshot_can_be_retried_but_an_external_change_cannot(
        int proxyEnable,
        string proxyServer,
        int originalProxyEnable,
        bool expected)
    {
        Assert.Equal(expected, TitaniumSystemProxySnapshotProvider.WindowsProxyIsOwned(
            new WindowsProxyInfo(proxyEnable, null, proxyServer, "localhost;127.0.0.1;::1"),
            new WindowsProxyInfo(originalProxyEnable, null, "http=corp.example:80;https=secure.example:443", ""),
            "127.0.0.1:8080",
            "localhost;127.0.0.1;::1"));
    }

    [Fact]
    public void Fully_restored_settings_are_not_owned()
    {
        Assert.False(TitaniumSystemProxySnapshotProvider.WindowsProxyIsOwned(
            new WindowsProxyInfo(1, null, "http=corp.example:80;https=secure.example:443", ""),
            new WindowsProxyInfo(1, null, "http=corp.example:80;https=secure.example:443", ""),
            "127.0.0.1:8080",
            "localhost;127.0.0.1;::1"));
    }

    [Theory]
    [InlineData(ProxyProtocolType.AllHttp, true)]
    [InlineData(ProxyProtocolType.Http, false)]
    [InlineData(ProxyProtocolType.Https, false)]
    [InlineData(ProxyProtocolType.None, false)]
    public void Unix_ownership_requires_both_http_and_https_to_still_point_at_the_proxy(ProxyProtocolType owned, bool expected)
    {
        Assert.Equal(expected, TitaniumSystemProxySnapshotProvider.UnixProxyIsOwned(owned));
    }
}
