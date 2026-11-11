using System.Text.Json;
using AiProxy.Hosting.Integration;
using Titanium.Web.Proxy.Models;

namespace AiProxy.Tests.Unit;

public sealed class UnixProxyOwnershipTests
{
    private const string Corporate = "corp.example:80";
    private const string Secure = "secure.example:443";
    private const string Proxy = "127.0.0.1:8080";
    private const string OriginalBypass = "*.local";
    private const string AppliedBypass = "*.local\nlocalhost\n127.0.0.1\n::1";

    private static readonly string MacOriginal = MacSnapshot(Corporate, Secure, OriginalBypass);
    private static readonly string MacApplied = MacSnapshot(Proxy, Proxy, AppliedBypass);

    [Theory]
    [InlineData(Proxy, Proxy, AppliedBypass, true)]
    [InlineData(Corporate, Proxy, AppliedBypass, true)]
    [InlineData(Proxy, Secure, OriginalBypass, true)]
    [InlineData(Corporate, Secure, AppliedBypass, true)]
    [InlineData(Proxy, "other.example:443", AppliedBypass, false)]
    [InlineData("other.example:80", Proxy, AppliedBypass, false)]
    [InlineData(Proxy, Proxy, "corp.internal", false)]
    [InlineData(Corporate, Secure, OriginalBypass, false)]
    public void A_partly_restored_mac_snapshot_can_be_retried_but_an_external_change_cannot(
        string http,
        string https,
        string bypass,
        bool expected)
    {
        Assert.Equal(expected, IsOwned(MacSnapshot(http, https, bypass), MacApplied, MacOriginal));
    }

    [Fact]
    public void A_proxy_turned_off_by_someone_else_is_not_owned()
    {
        var current = MacSnapshot(Proxy, Proxy, AppliedBypass, httpEnabled: false);

        Assert.False(IsOwned(current, MacApplied, MacOriginal));
    }

    [Fact]
    public void The_capturing_process_environment_is_not_compared()
    {
        var current = LinuxSnapshot(Gnome("manual", Proxy, Proxy, "['localhost']"), env: "http://elsewhere:3128");
        var applied = LinuxSnapshot(Gnome("manual", Proxy, Proxy, "['localhost']"), env: "http://127.0.0.1:8080");
        var original = LinuxSnapshot(Gnome("none", Corporate, Secure, "[]"), env: null);

        Assert.True(IsOwned(current, applied, original));
    }

    [Theory]
    [InlineData("manual", Corporate, Proxy, "['localhost']", true)]
    [InlineData("none", Corporate, Secure, "['localhost']", true)]
    [InlineData("manual", Proxy, Proxy, "['corp.internal']", false)]
    [InlineData("auto", Proxy, Proxy, "['localhost']", false)]
    [InlineData("none", Corporate, Secure, "[]", false)]
    public void A_partly_restored_gnome_snapshot_can_be_retried_but_an_external_change_cannot(
        string mode,
        string http,
        string https,
        string ignoreHosts,
        bool expected)
    {
        var applied = LinuxSnapshot(Gnome("manual", Proxy, Proxy, "['localhost']"));
        var original = LinuxSnapshot(Gnome("none", Corporate, Secure, "[]"));

        Assert.Equal(expected, IsOwned(LinuxSnapshot(Gnome(mode, http, https, ignoreHosts)), applied, original));
    }

    [Theory]
    [InlineData(ProxyProtocolType.AllHttp, true)]
    [InlineData(ProxyProtocolType.Http, false)]
    [InlineData(ProxyProtocolType.Https, false)]
    [InlineData(ProxyProtocolType.None, false)]
    public void A_marker_without_applied_settings_keeps_the_both_protocols_rule(ProxyProtocolType owned, bool expected)
    {
        var owner = TitaniumSystemProxySnapshotProvider.UnixProxyIsOwned(
            MacOriginal,
            null,
            () => throw new InvalidOperationException("Current settings must not be read without applied settings."),
            () => owned);

        Assert.Equal(expected, owner);
    }

    [Fact]
    public void Applied_settings_that_changed_no_compared_value_fall_back_to_the_both_protocols_rule()
    {
        var applied = LinuxSnapshot(null, env: "http://127.0.0.1:8080");
        var original = LinuxSnapshot(null, env: null);

        var owner = TitaniumSystemProxySnapshotProvider.UnixProxyIsOwned(
            original,
            applied,
            () => throw new InvalidOperationException("Applied settings without a compared change carry no evidence."),
            () => ProxyProtocolType.AllHttp);

        Assert.True(owner);
    }

    private static bool IsOwned(string current, string applied, string original)
    {
        return TitaniumSystemProxySnapshotProvider.UnixProxyIsOwned(original, applied, () => current, () => ProxyProtocolType.None);
    }

    private static string MacSnapshot(string http, string https, string bypass, bool httpEnabled = true)
    {
        var httpEndpoint = HostPort.Parse(http);
        var httpsEndpoint = HostPort.Parse(https);
        var services = new[]
        {
            new Dictionary<string, object?>
            {
                ["Service"] = "Wi-Fi",
                ["HttpEnabled"] = httpEnabled,
                ["HttpHost"] = httpEndpoint.Host,
                ["HttpPort"] = httpEndpoint.Port,
                ["HttpsEnabled"] = true,
                ["HttpsHost"] = httpsEndpoint.Host,
                ["HttpsPort"] = httpsEndpoint.Port,
                ["SocksEnabled"] = false,
                ["SocksHost"] = "",
                ["SocksPort"] = 0,
                ["AutoProxyEnabled"] = false,
                ["AutoProxyUrl"] = "(null)",
                ["AutoDiscovery"] = false,
                ["BypassDomains"] = bypass
            }
        };
        return Snapshot("Titanium.Web.Proxy.Helpers.MacOsSystemProxyBackend", new Dictionary<string, object?>
        {
            ["_original"] = services,
            ["_hasSnapshot"] = true
        });
    }

    private static Dictionary<string, object?> Gnome(string mode, string http, string https, string ignoreHosts)
    {
        var httpEndpoint = HostPort.Parse(http);
        var httpsEndpoint = HostPort.Parse(https);
        return new Dictionary<string, object?>
        {
            ["Mode"] = mode,
            ["HttpHost"] = httpEndpoint.Host,
            ["HttpPort"] = httpEndpoint.Port,
            ["HttpEnabled"] = mode == "manual",
            ["HttpsHost"] = httpsEndpoint.Host,
            ["HttpsPort"] = httpsEndpoint.Port,
            ["IgnoreHosts"] = ignoreHosts
        };
    }

    private static string LinuxSnapshot(Dictionary<string, object?>? gnome, string? env = null)
    {
        return Snapshot("Titanium.Web.Proxy.Helpers.LinuxSystemProxyBackend", new Dictionary<string, object?>
        {
            ["_originalEnv"] = new Dictionary<string, string?> { ["http_proxy"] = env },
            ["_gnome"] = gnome,
            ["_kde"] = null,
            ["_hasSnapshot"] = true
        });
    }

    private static string Snapshot(string backendType, Dictionary<string, object?> fields)
    {
        var values = fields.ToDictionary(field => field.Key, field => JsonSerializer.Serialize(field.Value));
        return JsonSerializer.Serialize(new { version = 1, backendType, values });
    }

    private sealed record HostPort(string Host, int Port)
    {
        public static HostPort Parse(string endpoint)
        {
            var separator = endpoint.LastIndexOf(':');
            return new HostPort(endpoint[..separator], int.Parse(endpoint[(separator + 1)..]));
        }
    }
}
