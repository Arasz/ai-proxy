using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Hosting.Proxy;
using Titanium.Web.Proxy;

namespace AiProxy.Hosting.Integration;

/// <summary>
///     Cleans a captured restore target: each active HTTP or HTTPS value that points at a stale ai-proxy endpoint takes the
///     value saved before that run, or is turned off. Works on the serialized snapshot, so every platform is testable anywhere.
/// </summary>
internal static class StaleProxyValues
{
    private const string LinuxBackend = "Titanium.Web.Proxy.Helpers.LinuxSystemProxyBackend";
    private const string WindowsBackend = "Titanium.Web.Proxy.Helpers.SystemProxyManager";
    private static readonly string[] WindowsProtocols = ["http", "https"];

    /// <summary>
    ///     The cleaned target. When nothing pointed at <paramref name="stale" />, it is <paramref name="current" /> as captured; when the
    ///     capture could not be cleaned, it is <paramref name="current" /> too, with <see cref="RestoreTarget.CleanFailure" /> saying why.
    /// </summary>
    public static RestoreTarget Replace(string current, IReadOnlyCollection<IPEndPoint> stale, string? previous) =>
        Replace(current, stale, previous, WindowsInfoType);

    /// <summary><see cref="Replace(string, IReadOnlyCollection{IPEndPoint}, string?)" /> with Titanium's Windows proxy type looked up by <paramref name="windowsInfoType" />.</summary>
    internal static RestoreTarget Replace(string current, IReadOnlyCollection<IPEndPoint> stale, string? previous, Func<Type> windowsInfoType)
    {
        try
        {
            var snapshot = Snapshot.Parse(current) ?? throw Unexpected();

            // Each backend reads only its own fields, so a previous snapshot from another backend contributes nothing.
            var before = Snapshot.Parse(previous);
            var matched = new Matches(stale);
            var changed = snapshot.BackendType switch
            {
                TitaniumSystemProxySnapshotProvider.MacBackendType => CleanMac(snapshot, before, matched),
                LinuxBackend => CleanGnome(snapshot, before, matched) | CleanKde(snapshot, before, matched),
                WindowsBackend => CleanWindows(snapshot, before, matched, windowsInfoType),
                _ => throw Unexpected()
            };
            return changed ? new RestoreTarget(snapshot.ToJson(), matched.Found) : new RestoreTarget(current, []);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Any failure here, a Titanium type or member gone missing included, must not stop the start: keep the capture as taken.
            return new RestoreTarget(current, []) { CleanFailure = exception };
        }
    }

    /// <summary>The endpoints a value may still point at: the previous run's, when it parses, then this run's own.</summary>
    public static IReadOnlyList<IPEndPoint> Endpoints(IPEndPoint own, string? previousEndpoint)
    {
        return IPEndPoint.TryParse(previousEndpoint ?? "", out var previous) && !previous.Equals(own) ? [previous, own] : [own];
    }

    /// <summary>macOS: an enabled stale proxy takes the same service's previous triple, or is disabled.</summary>
    private static bool CleanMac(Snapshot snapshot, Snapshot? previous, Matches matched)
    {
        var services = Required(snapshot.Field("_original") as JsonArray);
        var previousServices = (previous?.Field("_original") as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var changed = false;
        foreach (var service in services.OfType<JsonObject>())
        {
            var name = RequiredText(service, "Service");
            var previousService = previousServices.FirstOrDefault(item => Text(item, "Service") == name);
            foreach (var protocol in TitaniumSystemProxySnapshotProvider.MacProtocols)
            {
                if (!RequiredBool(service, $"{protocol}Enabled") ||
                    !matched.Add(RequiredText(service, $"{protocol}Host"), RequiredInt(service, $"{protocol}Port")))
                {
                    continue;
                }

                changed = true;
                if (previousService is not null && ReadMac(previousService, protocol) is { } value && !matched.PointsAt(value.Host, value.Port))
                {
                    service[$"{protocol}Enabled"] = value.Enabled;
                    service[$"{protocol}Host"] = value.Host;
                    service[$"{protocol}Port"] = value.Port;
                }
                else
                {
                    service[$"{protocol}Enabled"] = false;
                }
            }
        }

        snapshot.SetField("_original", services);
        return changed;
    }

    /// <summary>
    ///     GNOME: in manual mode, a stale host and port take the previous ones while those were in manual mode too, or are emptied;
    ///     with no host left, the mode turns off.
    /// </summary>
    private static bool CleanGnome(Snapshot snapshot, Snapshot? previous, Matches matched)
    {
        if (snapshot.Field("_gnome") is not JsonObject gnome || RequiredText(gnome, "Mode") != "manual")
        {
            return false;
        }

        var previousGnome = previous?.Field("_gnome") is JsonObject before && Text(before, "Mode") == "manual" ? before : null;
        var httpStale = matched.Add(RequiredText(gnome, "HttpHost"), RequiredInt(gnome, "HttpPort"));
        var httpsStale = matched.Add(RequiredText(gnome, "HttpsHost"), RequiredInt(gnome, "HttpsPort"));
        if (!httpStale && !httpsStale)
        {
            return false;
        }

        if (httpStale)
        {
            ReplaceGnome(gnome, previousGnome, "Http", matched);
        }

        if (httpsStale)
        {
            ReplaceGnome(gnome, previousGnome, "Https", matched);
        }

        if (RequiredText(gnome, "HttpHost").Length == 0 && RequiredText(gnome, "HttpsHost").Length == 0)
        {
            gnome["Mode"] = "none";
        }

        snapshot.SetField("_gnome", gnome);
        return true;
    }

    /// <summary>Copies one GNOME protocol's host and port from <paramref name="previous" />, or empties it.</summary>
    private static void ReplaceGnome(JsonObject gnome, JsonObject? previous, string protocol, Matches matched)
    {
        if (previous is not null && Text(previous, $"{protocol}Host") is { } host && Int(previous, $"{protocol}Port") is { } port &&
            !matched.PointsAt(host, port))
        {
            gnome[$"{protocol}Host"] = host;
            gnome[$"{protocol}Port"] = port;
            if (protocol == "Http")
            {
                gnome["HttpEnabled"] = Bool(previous, "HttpEnabled") is true;
            }

            return;
        }

        gnome[$"{protocol}Host"] = "";
        gnome[$"{protocol}Port"] = 0;
        if (protocol == "Http")
        {
            gnome["HttpEnabled"] = false;
        }
    }

    /// <summary>
    ///     KDE: in manual mode (<c>ProxyType</c> 1), a stale proxy URL takes the previous one while that was in manual mode too, or is
    ///     emptied; with no URL left, the type turns off.
    /// </summary>
    private static bool CleanKde(Snapshot snapshot, Snapshot? previous, Matches matched)
    {
        if (snapshot.Field("_kde") is not JsonObject kde || RequiredText(kde, "ProxyType") != "1")
        {
            return false;
        }

        var previousKde = previous?.Field("_kde") is JsonObject before && Text(before, "ProxyType") == "1" ? before : null;
        var httpStale = matched.Add(RequiredText(kde, "HttpProxy"));
        var httpsStale = matched.Add(RequiredText(kde, "HttpsProxy"));
        if (!httpStale && !httpsStale)
        {
            return false;
        }

        if (httpStale)
        {
            ReplaceKde(kde, previousKde, "HttpProxy", matched);
        }

        if (httpsStale)
        {
            ReplaceKde(kde, previousKde, "HttpsProxy", matched);
        }

        if (RequiredText(kde, "HttpProxy").Length == 0 && RequiredText(kde, "HttpsProxy").Length == 0)
        {
            kde["ProxyType"] = "0";
        }

        snapshot.SetField("_kde", kde);
        return true;
    }

    /// <summary>Copies one KDE proxy URL from <paramref name="previous" />, or empties it.</summary>
    private static void ReplaceKde(JsonObject kde, JsonObject? previous, string key, Matches matched)
    {
        kde[key] = previous is not null && Text(previous, key) is { } value && !matched.PointsAtUrl(value) ? value : "";
    }

    /// <summary>
    ///     Windows: while the manual proxy is active, a stale <c>ProxyServer</c> entry takes the previous active entry for its protocol,
    ///     else the previous unnamed address, or is dropped. A stale unnamed entry takes the previous unnamed one, else the previous
    ///     http/https entries it stood for. With no http or https entry left, the proxy turns off, as Titanium's own removal does.
    /// </summary>
    private static bool CleanWindows(Snapshot snapshot, Snapshot? previous, Matches matched, Func<Type> windowsInfoType)
    {
        if (snapshot.Field("originalValues") is not JsonObject info ||
            !TitaniumSystemProxySnapshotProvider.WindowsProxyIsActive(Int(info, "ProxyEnable"), Text(info, "AutoConfigUrl")))
        {
            return false;
        }

        var entries = WindowsEntry.Parse(Text(info, "ProxyServer"));
        var previousEntries = previous?.Field("originalValues") is JsonObject before &&
                              TitaniumSystemProxySnapshotProvider.WindowsProxyIsActive(Int(before, "ProxyEnable"), Text(before, "AutoConfigUrl"))
            ? WindowsEntry.Parse(Text(before, "ProxyServer")).Where(entry => !matched.PointsAtUrl(entry.Address)).ToArray()
            : [];
        var result = new List<string>();
        var changed = false;
        foreach (var entry in entries)
        {
            if (!matched.Add(entry.Address))
            {
                result.Add(entry.Text);
                continue;
            }

            changed = true;
            if (entry.Protocol is not null)
            {
                if (previousEntries.FirstOrDefault(item => SameProtocol(item.Protocol, entry.Protocol)) is { } named)
                {
                    result.Add(named.Text);
                }
                else if (previousEntries.FirstOrDefault(item => item.Protocol is null) is { } unnamedFallback)
                {
                    // Titanium reads an unnamed entry as both http and https, so it stands for the missing named one.
                    result.Add($"{entry.Protocol}={unnamedFallback.Address}");
                }
            }
            else if (previousEntries.FirstOrDefault(item => item.Protocol is null) is { } unnamed)
            {
                result.Add(unnamed.Text);
            }
            else
            {
                result.AddRange(WindowsProtocols
                    .SelectMany(protocol => previousEntries.Where(item => SameProtocol(item.Protocol, protocol)).Take(1))
                    .Select(item => item.Text));
            }
        }

        if (!changed)
        {
            return false;
        }

        var proxyServer = string.Join(';', result);
        info["ProxyServer"] = proxyServer;
        if (!WindowsEntry.Parse(proxyServer).Any(entry => entry.ServesHttp))
        {
            info["ProxyEnable"] = 0;
        }

        // Re-encode through Titanium's own type, so the derived Proxies and BypassList match what a capture would write.
        var type = windowsInfoType();
        var rebuilt = SystemProxySnapshotCodec.Read(info.ToJsonString(), type);
        snapshot.SetField("originalValues", JsonNode.Parse(SystemProxySnapshotCodec.Write(rebuilt, type)));
        return true;
    }

    /// <summary>Titanium's internal Windows <c>ProxyInfo</c> type; throws when the installed Titanium no longer has it.</summary>
    private static Type WindowsInfoType() => typeof(ProxyServer).Assembly.GetType("Titanium.Web.Proxy.Helpers.ProxyInfo", true)!;

    /// <summary>True when both protocol names are set and equal, ignoring case.</summary>
    private static bool SameProtocol(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>A macOS service's triple for <paramref name="protocol" />, or null when any part is missing.</summary>
    private static MacValue? ReadMac(JsonObject service, string protocol)
    {
        return Bool(service, $"{protocol}Enabled") is { } enabled && Text(service, $"{protocol}Host") is { } host && Int(service, $"{protocol}Port") is { } port
            ? new MacValue(enabled, host, port)
            : null;
    }

    /// <summary>The string value of <paramref name="name" />, or null when it is missing or not a string.</summary>
    private static string? Text(JsonObject item, string name) => item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>The integer value of <paramref name="name" />, or null when it is missing or not an integer.</summary>
    private static int? Int(JsonObject item, string name) => item[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    /// <summary>The boolean value of <paramref name="name" />, or null when it is missing or not a boolean.</summary>
    private static bool? Bool(JsonObject item, string name) => item[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    /// <summary>The string value of <paramref name="name" />; throws when the capture lacks it.</summary>
    private static string RequiredText(JsonObject item, string name) => Required(Text(item, name));

    /// <summary>The integer value of <paramref name="name" />; throws when the capture lacks it.</summary>
    private static int RequiredInt(JsonObject item, string name) => Int(item, name) ?? throw Unexpected();

    /// <summary>The boolean value of <paramref name="name" />; throws when the capture lacks it.</summary>
    private static bool RequiredBool(JsonObject item, string name) => Bool(item, name) ?? throw Unexpected();

    /// <summary>Returns <paramref name="value" />; throws when the capture lacks it.</summary>
    private static T Required<T>(T? value) where T : class => value ?? throw Unexpected();

    /// <summary>The failure for a capture whose shape this version does not know; <see cref="Replace(string, IReadOnlyCollection{IPEndPoint}, string?)" /> keeps the capture as taken.</summary>
    private static InvalidOperationException Unexpected() => new("The captured system proxy snapshot has an unexpected shape.");

    /// <summary>A macOS proxy: whether it is on, and where it points.</summary>
    private sealed record MacValue(bool Enabled, string Host, int Port);

    /// <summary>One WinINET <c>ProxyServer</c> entry: its protocol (null when unnamed), its address and its original text.</summary>
    private sealed record WindowsEntry(string? Protocol, string Address, string Text)
    {
        /// <summary>True for an http, https or unnamed entry: the ones Titanium reads as an HTTP proxy.</summary>
        public bool ServesHttp => Protocol is null || WindowsProtocols.Any(protocol => SameProtocol(Protocol, protocol));

        /// <summary>The entries of a <c>ProxyServer</c> value, in order.</summary>
        public static WindowsEntry[] Parse(string? proxyServer)
        {
            return (proxyServer ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => entry.Split('=', 2, StringSplitOptions.TrimEntries) is [var protocol, var address]
                    ? new WindowsEntry(protocol, address, entry)
                    : new WindowsEntry(null, entry, entry))
                .ToArray();
        }
    }

    /// <summary>The stale endpoints, and those that values were found pointing at, in the order found.</summary>
    private sealed class Matches(IReadOnlyCollection<IPEndPoint> stale)
    {
        private readonly List<IPEndPoint> _found = [];

        /// <summary>The stale endpoints at least one value pointed at.</summary>
        public IReadOnlyList<IPEndPoint> Found => _found;

        /// <summary>Records and returns whether <paramref name="host" />:<paramref name="port" /> points at a stale endpoint.</summary>
        public bool Add(string host, int port) => Record(Find(host, port));

        /// <summary>Records and returns whether a proxy URL such as <c>http://localhost:8080</c>, or a bare <c>host:port</c>, points at a stale endpoint.</summary>
        public bool Add(string url) => TryParse(url, out var host, out var port) && Record(Find(host, port));

        /// <summary>True when <paramref name="host" />:<paramref name="port" /> points at a stale endpoint.</summary>
        public bool PointsAt(string host, int port) => Find(host, port) is not null;

        /// <summary>True when a proxy URL points at a stale endpoint.</summary>
        public bool PointsAtUrl(string url) => TryParse(url, out var host, out var port) && PointsAt(host, port);

        /// <summary>
        ///     The stale endpoint on <paramref name="port" /> that <paramref name="host" /> names: <c>localhost</c>, any loopback address,
        ///     or the endpoint's own address. Narrower than Titanium's local-host test, so a LAN proxy on the same port is never touched.
        /// </summary>
        private IPEndPoint? Find(string host, int port)
        {
            var name = host.Trim('[', ']');
            var address = IPAddress.TryParse(name, out var parsed) ? parsed : null;
            return stale.FirstOrDefault(endpoint => endpoint.Port == port &&
                                                    (name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                                                     (address is not null && (IPAddress.IsLoopback(address) || address.Equals(endpoint.Address)))));
        }

        /// <summary>Adds <paramref name="endpoint" /> to the found list; true when it is not null.</summary>
        private bool Record(IPEndPoint? endpoint)
        {
            if (endpoint is null)
            {
                return false;
            }

            if (!_found.Contains(endpoint))
            {
                _found.Add(endpoint);
            }

            return true;
        }

        /// <summary>Reads the host and port of a proxy URL, assuming <c>http://</c> when it names no scheme.</summary>
        private static bool TryParse(string url, out string host, out int port)
        {
            var text = url.Contains("://", StringComparison.Ordinal) ? url : $"http://{url}";
            var parsed = Uri.TryCreate(text, UriKind.Absolute, out var uri);
            host = parsed ? uri!.Host : "";
            port = parsed ? uri!.Port : 0;
            return parsed && host.Length > 0;
        }
    }

    /// <summary>A parsed snapshot document whose field values are JSON text.</summary>
    private sealed class Snapshot(JsonObject root, string backendType, JsonObject values)
    {
        /// <summary>The Titanium backend type the snapshot was captured from.</summary>
        public string BackendType => backendType;

        /// <summary>The snapshot, or null when <paramref name="json" /> is missing or not a snapshot.</summary>
        public static Snapshot? Parse(string? json)
        {
            try
            {
                return json is not null && JsonNode.Parse(json) is JsonObject root && Text(root, "backendType") is { } type && root["values"] is JsonObject values
                    ? new Snapshot(root, type, values)
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>The decoded value of field <paramref name="name" />, or null when it is missing or JSON null.</summary>
        public JsonNode? Field(string name) => Text(values, name) is { } json ? JsonNode.Parse(json) : null;

        /// <summary>Stores <paramref name="value" /> as the JSON text of field <paramref name="name" />.</summary>
        public void SetField(string name, JsonNode? value) => values[name] = value?.ToJsonString() ?? "null";

        /// <summary>The serialized snapshot.</summary>
        public string ToJson() => root.ToJsonString();
    }
}
