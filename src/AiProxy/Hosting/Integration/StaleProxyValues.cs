using System.Net;
using System.Reflection;
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
    private const string MacBackend = "Titanium.Web.Proxy.Helpers.MacOsSystemProxyBackend";
    private const string LinuxBackend = "Titanium.Web.Proxy.Helpers.LinuxSystemProxyBackend";
    private const string WindowsBackend = "Titanium.Web.Proxy.Helpers.SystemProxyManager";
    private static readonly string[] MacProtocols = ["Http", "Https"];
    private static readonly string[] WindowsProtocols = ["http", "https"];

    /// <summary>The cleaned target, or null when nothing pointed at <paramref name="stale" /> or the capture has an unexpected shape.</summary>
    public static RestoreTarget? Replace(string current, IReadOnlyCollection<IPEndPoint> stale, string? previous)
    {
        try
        {
            if (Snapshot.Parse(current) is not { } snapshot)
            {
                return null;
            }

            // Each backend reads only its own fields, so a previous snapshot from another backend contributes nothing.
            var before = Snapshot.Parse(previous);
            var matched = new Matches(stale);
            var changed = snapshot.BackendType switch
            {
                MacBackend => CleanMac(snapshot, before, matched),
                LinuxBackend => CleanGnome(snapshot, before, matched) | CleanKde(snapshot, before, matched),
                WindowsBackend => CleanWindows(snapshot, before, matched),
                _ => false
            };
            return changed ? new RestoreTarget(snapshot.ToJson(), matched.Found) : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException or
                                              TargetInvocationException or NotSupportedException)
        {
            return null;
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
            foreach (var protocol in MacProtocols)
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

    /// <summary>GNOME: in manual mode, a stale host and port take the previous ones or are emptied; the mode follows.</summary>
    private static bool CleanGnome(Snapshot snapshot, Snapshot? previous, Matches matched)
    {
        if (snapshot.Field("_gnome") is not JsonObject gnome || RequiredText(gnome, "Mode") != "manual")
        {
            return false;
        }

        var previousGnome = previous?.Field("_gnome") as JsonObject;
        var httpStale = matched.Add(RequiredText(gnome, "HttpHost"), RequiredInt(gnome, "HttpPort"));
        var httpsStale = matched.Add(RequiredText(gnome, "HttpsHost"), RequiredInt(gnome, "HttpsPort"));
        if (!httpStale && !httpsStale)
        {
            return false;
        }

        var httpRestored = httpStale && ReplaceGnome(gnome, previousGnome, "Http", matched);
        var httpsRestored = httpsStale && ReplaceGnome(gnome, previousGnome, "Https", matched);
        if (httpStale && httpsStale && httpRestored && httpsRestored)
        {
            gnome["Mode"] = Text(previousGnome!, "Mode") ?? "manual";
        }
        else if (RequiredText(gnome, "HttpHost").Length == 0 && RequiredText(gnome, "HttpsHost").Length == 0)
        {
            gnome["Mode"] = "none";
        }

        snapshot.SetField("_gnome", gnome);
        return true;
    }

    /// <summary>Copies one GNOME protocol's host and port from <paramref name="previous" />, or empties it; true when copied.</summary>
    private static bool ReplaceGnome(JsonObject gnome, JsonObject? previous, string protocol, Matches matched)
    {
        var host = previous is null ? null : Text(previous, $"{protocol}Host");
        var port = previous is null ? null : Int(previous, $"{protocol}Port");
        var usable = host is not null && port is not null && !matched.PointsAt(host, port.Value);
        gnome[$"{protocol}Host"] = usable ? host : "";
        gnome[$"{protocol}Port"] = usable ? port : 0;
        if (protocol == "Http")
        {
            gnome["HttpEnabled"] = usable && Bool(previous!, "HttpEnabled") is true;
        }

        return usable;
    }

    /// <summary>KDE: in manual mode (<c>ProxyType</c> 1), a stale proxy URL takes the previous one or is emptied; the type follows.</summary>
    private static bool CleanKde(Snapshot snapshot, Snapshot? previous, Matches matched)
    {
        if (snapshot.Field("_kde") is not JsonObject kde || RequiredText(kde, "ProxyType") != "1")
        {
            return false;
        }

        var previousKde = previous?.Field("_kde") as JsonObject;
        var httpStale = matched.Add(RequiredText(kde, "HttpProxy"));
        var httpsStale = matched.Add(RequiredText(kde, "HttpsProxy"));
        if (!httpStale && !httpsStale)
        {
            return false;
        }

        var httpRestored = httpStale && ReplaceKde(kde, previousKde, "HttpProxy", matched);
        var httpsRestored = httpsStale && ReplaceKde(kde, previousKde, "HttpsProxy", matched);
        if (httpStale && httpsStale && httpRestored && httpsRestored)
        {
            kde["ProxyType"] = Text(previousKde!, "ProxyType") ?? "1";
        }
        else if (RequiredText(kde, "HttpProxy").Length == 0 && RequiredText(kde, "HttpsProxy").Length == 0)
        {
            kde["ProxyType"] = "0";
        }

        snapshot.SetField("_kde", kde);
        return true;
    }

    /// <summary>Copies one KDE proxy URL from <paramref name="previous" />, or empties it; true when copied.</summary>
    private static bool ReplaceKde(JsonObject kde, JsonObject? previous, string key, Matches matched)
    {
        var value = previous is null ? null : Text(previous, key);
        var usable = value is not null && !matched.PointsAtUrl(value);
        kde[key] = usable ? value : "";
        return usable;
    }

    /// <summary>
    ///     Windows: while the manual proxy is active, a stale <c>ProxyServer</c> entry takes the previous entry for its protocol or is
    ///     dropped. A stale unnamed entry takes the previous unnamed one, else the previous http/https entries it stood for.
    /// </summary>
    private static bool CleanWindows(Snapshot snapshot, Snapshot? previous, Matches matched)
    {
        if (snapshot.Field("originalValues") is not JsonObject info ||
            !TitaniumSystemProxySnapshotProvider.WindowsProxyIsActive(Int(info, "ProxyEnable"), Text(info, "AutoConfigUrl")))
        {
            return false;
        }

        var entries = WindowsEntry.Parse(Text(info, "ProxyServer"));
        var previousEntries = WindowsEntry.Parse((previous?.Field("originalValues") as JsonObject) is { } before ? Text(before, "ProxyServer") : null)
            .Where(entry => !matched.PointsAtUrl(entry.Address))
            .ToArray();
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
                result.AddRange(previousEntries.Where(item => SameProtocol(item.Protocol, entry.Protocol)).Select(item => item.Text).Take(1));
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

        info["ProxyServer"] = string.Join(';', result);
        if (result.Count == 0)
        {
            info["ProxyEnable"] = 0;
        }

        // Re-encode through Titanium's own type, so the derived Proxies and BypassList match what a capture would write.
        var type = typeof(ProxyServer).Assembly.GetType("Titanium.Web.Proxy.Helpers.ProxyInfo", true)!;
        var rebuilt = SystemProxySnapshotCodec.Read(info.ToJsonString(), type);
        snapshot.SetField("originalValues", JsonNode.Parse(SystemProxySnapshotCodec.Write(rebuilt, type)));
        return true;
    }

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

    /// <summary>The failure for a capture whose shape this version does not know; <see cref="Replace" /> turns it into "no change".</summary>
    private static InvalidOperationException Unexpected() => new("The captured system proxy snapshot has an unexpected shape.");

    /// <summary>A macOS proxy: whether it is on, and where it points.</summary>
    private sealed record MacValue(bool Enabled, string Host, int Port);

    /// <summary>One WinINET <c>ProxyServer</c> entry: its protocol (null when unnamed), its address and its original text.</summary>
    private sealed record WindowsEntry(string? Protocol, string Address, string Text)
    {
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
