using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Hosting.Integration;
using Titanium.Web.Proxy;

namespace AiProxy.Tests.Unit;

/// <summary>Builds Titanium platform snapshots in the serialized form the snapshot provider writes.</summary>
internal static class ProxySnapshots
{
    public const string MacBackend = "Titanium.Web.Proxy.Helpers.MacOsSystemProxyBackend";
    public const string LinuxBackend = "Titanium.Web.Proxy.Helpers.LinuxSystemProxyBackend";
    public const string WindowsBackend = "Titanium.Web.Proxy.Helpers.SystemProxyManager";

    /// <summary>One macOS network service with its HTTP and HTTPS proxy state.</summary>
    public static Dictionary<string, object?> MacService(string service, MacProxy http, MacProxy https) => new()
    {
        ["Service"] = service,
        ["HttpEnabled"] = http.Enabled,
        ["HttpHost"] = http.Host,
        ["HttpPort"] = http.Port,
        ["HttpsEnabled"] = https.Enabled,
        ["HttpsHost"] = https.Host,
        ["HttpsPort"] = https.Port,
        ["SocksEnabled"] = false,
        ["SocksHost"] = "",
        ["SocksPort"] = 0,
        ["AutoProxyEnabled"] = false,
        ["AutoProxyUrl"] = "(null)",
        ["AutoDiscovery"] = false,
        ["BypassDomains"] = "*.local"
    };

    /// <summary>A macOS snapshot holding <paramref name="services" />.</summary>
    public static string Mac(params Dictionary<string, object?>[] services) =>
        Snapshot(MacBackend, new Dictionary<string, object?> { ["_original"] = services, ["_hasSnapshot"] = true });

    /// <summary>The GNOME part of a Linux snapshot.</summary>
    public static Dictionary<string, object?> Gnome(string mode, string httpHost, int httpPort, string httpsHost, int httpsPort) => new()
    {
        ["Mode"] = mode,
        ["HttpHost"] = httpHost,
        ["HttpPort"] = httpPort,
        ["HttpEnabled"] = mode == "manual",
        ["HttpsHost"] = httpsHost,
        ["HttpsPort"] = httpsPort,
        ["IgnoreHosts"] = "['localhost']"
    };

    /// <summary>The KDE part of a Linux snapshot.</summary>
    public static Dictionary<string, object?> Kde(string proxyType, string httpProxy, string httpsProxy) => new()
    {
        ["ProxyType"] = proxyType,
        ["HttpProxy"] = httpProxy,
        ["HttpsProxy"] = httpsProxy,
        ["NoProxyFor"] = "localhost"
    };

    /// <summary>A Linux snapshot with optional GNOME and KDE settings.</summary>
    public static string Linux(Dictionary<string, object?>? gnome, Dictionary<string, object?>? kde = null) =>
        Snapshot(LinuxBackend, new Dictionary<string, object?>
        {
            ["_originalEnv"] = new Dictionary<string, string?> { ["http_proxy"] = null },
            ["_gnome"] = gnome,
            ["_kde"] = kde,
            ["_hasSnapshot"] = true
        });

    /// <summary>A Windows snapshot encoded from Titanium's own <c>ProxyInfo</c>, derived fields included.</summary>
    public static string WindowsSnapshot(int? proxyEnable, string? proxyServer, string? autoConfigUrl = null, string? proxyOverride = "<local>")
    {
        var type = typeof(ProxyServer).Assembly.GetType("Titanium.Web.Proxy.Helpers.ProxyInfo", true)!;
        var info = Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null,
            [null, autoConfigUrl, proxyEnable, proxyServer, proxyOverride],
            null);
        var values = new Dictionary<string, string> { ["originalValues"] = SystemProxySnapshotCodec.Write(info, type) };
        return JsonSerializer.Serialize(new { version = 1, backendType = WindowsBackend, values });
    }

    /// <summary>A snapshot of <paramref name="backendType" /> whose fields are serialized one by one.</summary>
    public static string Snapshot(string backendType, Dictionary<string, object?> fields)
    {
        var values = fields.ToDictionary(field => field.Key, field => JsonSerializer.Serialize(field.Value));
        return JsonSerializer.Serialize(new { version = 1, backendType, values });
    }

    /// <summary>The decoded JSON of one snapshot field.</summary>
    public static JsonNode? Field(string snapshot, string name)
    {
        var root = JsonNode.Parse(snapshot)!;
        return JsonNode.Parse(root["values"]![name]!.GetValue<string>());
    }

    /// <summary>A macOS service's proxy triple for <paramref name="protocol" /> ("Http" or "Https").</summary>
    public static MacProxy MacProxyOf(string snapshot, string service, string protocol)
    {
        var entry = Field(snapshot, "_original")!.AsArray().Single(item => item!["Service"]!.GetValue<string>() == service)!;
        return new MacProxy(
            entry[$"{protocol}Enabled"]!.GetValue<bool>(),
            entry[$"{protocol}Host"]!.GetValue<string>(),
            entry[$"{protocol}Port"]!.GetValue<int>());
    }
}

/// <summary>A macOS proxy setting: whether it is on, and where it points.</summary>
internal sealed record MacProxy(bool Enabled, string Host, int Port)
{
    /// <summary>An enabled proxy at <paramref name="host" />:<paramref name="port" />.</summary>
    public static MacProxy On(string host, int port) => new(true, host, port);
}
