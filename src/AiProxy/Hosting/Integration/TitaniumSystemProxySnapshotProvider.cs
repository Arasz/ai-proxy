using System.Reflection;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Hosting.Proxy;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace AiProxy.Hosting.Integration;

/// <summary>Persists Titanium's platform snapshot so a separate guard process can restore it after a crash.</summary>
public sealed class TitaniumSystemProxySnapshotProvider(ProxyServer server) : ISystemProxySnapshotProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string UnnamedProtocol = "";

    /// <summary>The type name of Titanium's macOS backend, as a snapshot records it.</summary>
    internal const string MacBackendType = "Titanium.Web.Proxy.Helpers.MacOsSystemProxyBackend";

    /// <summary>The protocol prefixes of a macOS service's proxy fields, such as <c>HttpsEnabled</c>.</summary>
    internal static readonly string[] MacProtocols = ["Http", "Https"];

    /// <inheritdoc />
    public string Capture()
    {
        CaptureOriginalSettings(GetBackend(server));
        return Read();
    }

    /// <summary>Serializes the values the backend would restore now, without reading the system settings.</summary>
    internal string Read()
    {
        var backend = GetBackend(server);
        var values = SnapshotFieldNames().ToDictionary(
            name => name,
            name => SystemProxySnapshotCodec.Write(ReadField(backend, name), GetField(backend, name).FieldType),
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(new SnapshotDocument(1, backend.GetType().FullName!, values), JsonOptions);
    }

    /// <summary>True while the backend holds settings that stopping or exiting the server would restore.</summary>
    internal bool IsArmed => OperatingSystem.IsWindows()
        ? ReadField(GetBackend(server), "originalValues") is not null
        : ReadField(GetBackend(server), "_hasSnapshot") is true;

    /// <summary>Drops the backend's saved settings, so stopping or exiting the server restores nothing.</summary>
    internal void Disarm()
    {
        var backend = GetBackend(server);
        if (OperatingSystem.IsWindows())
        {
            GetField(backend, "originalValues").SetValue(backend, null);
            return;
        }

        GetField(backend, "_hasSnapshot").SetValue(backend, false);
    }

    /// <inheritdoc />
    public Task<bool> RestoreIfOwned(string endpoint, string snapshot, string? appliedSnapshot)
    {
        if (!IsOwned(endpoint, snapshot, appliedSnapshot))
        {
            return Task.FromResult(false);
        }

        HydrateSnapshot(snapshot);
        if (!IsOwned(endpoint, snapshot, appliedSnapshot))
        {
            return Task.FromResult(false);
        }

        var result = server.TryRestoreOriginalProxySettings();
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"The saved system proxy settings could not be restored: {result.Message}");
        }

        var restoredSnapshot = CaptureCurrentSettings();
        if (!SnapshotsMatch(snapshot, restoredSnapshot))
        {
            throw new InvalidOperationException("Titanium reported a successful restore, but the current system proxy settings do not match the saved snapshot.");
        }

        return Task.FromResult(true);
    }

    /// <summary>Returns whether HTTP or HTTPS still points at a local proxy on <paramref name="endpoint" />'s port, for markers without saved settings.</summary>
    public bool IsOwned(string endpoint)
    {
        if (!IPEndPoint.TryParse(endpoint, out var endpointAddress))
        {
            return false;
        }

        var backend = GetBackend(server);
        if (OperatingSystem.IsWindows())
        {
            var current = ReadWindowsProxyInfo(backend);
            return WindowsProxyIsActive(current.ProxyEnable, current.AutoConfigUrl) &&
                   HasOwnedProtocols(GetStaleLocalProxyProtocols(backend, endpointAddress.Port));
        }

        return HasOwnedProtocols(GetStaleLocalProxyProtocols(backend, endpointAddress.Port));
    }

    /// <inheritdoc />
    public bool IsOwned(string endpoint, string snapshot, string? appliedSnapshot)
    {
        if (!IPEndPoint.TryParse(endpoint, out var endpointAddress))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return UnixProxyIsOwned(
                snapshot,
                appliedSnapshot,
                CaptureCurrentSettings,
                () => GetStaleLocalProxyProtocols(GetBackend(server), endpointAddress.Port));
        }

        var original = ReadOriginalWindowsProxyInfo(snapshot);
        var expectedProxyOverride = TitaniumSystemProxyRegistrar.BypassSettings().BuildProxyOverride(original.ProxyOverride);
        return WindowsProxyIsOwned(ReadWindowsProxyInfo(GetBackend(server)), original, endpointAddress.ToString(), expectedProxyOverride);
    }

    /// <summary>True when a manual proxy is on and no PAC file overrides it.</summary>
    internal static bool WindowsProxyIsActive(int? proxyEnable, string? autoConfigUrl)
    {
        return proxyEnable == 1 && string.IsNullOrWhiteSpace(autoConfigUrl);
    }

    /// <summary>ai-proxy always sets HTTP and HTTPS together, so a single protocol still pointing at it means the other was changed.</summary>
    internal static bool UnixProxyIsOwned(ProxyProtocolType owned) => owned == ProxyProtocolType.AllHttp;

    /// <summary>
    ///     With the settings ai-proxy applied, each value must be ai-proxy's or the original's, so a partly applied restore can be retried;
    ///     without them (an older marker, or a crash before they were recorded), HTTP and HTTPS must both still point at the proxy.
    /// </summary>
    internal static bool UnixProxyIsOwned(string original, string? applied, Func<string> currentSettings, Func<ProxyProtocolType> ownedProtocols)
    {
        if (applied is null || !UnixProxySettings.Differ(applied, original))
        {
            return UnixProxyIsOwned(ownedProtocols());
        }

        return UnixProxySettings.IsOwned(currentSettings(), applied, original);
    }

    /// <summary>
    ///     True when every value is either what ai-proxy set or what the snapshot holds, and at least one is still ai-proxy's,
    ///     so a restore (or a retry of a partly applied one) never overwrites a change made by someone else.
    /// </summary>
    internal static bool WindowsProxyIsOwned(WindowsProxyInfo current, WindowsProxyInfo original, string endpoint, string expectedProxyOverride)
    {
        if (!IPEndPoint.TryParse(endpoint, out var expected))
        {
            return false;
        }

        var protocols = ParseProxyServer(current.ProxyServer);
        var originalProtocols = ParseProxyServer(original.ProxyServer);
        var httpOwned = ProxyMatches(protocols, "http", expected);
        var httpsOwned = ProxyMatches(protocols, "https", expected);
        var proxyOverride = current.ProxyOverride ?? string.Empty;
        var originalProxyOverride = original.ProxyOverride ?? string.Empty;
        var overrideOwned = string.Equals(proxyOverride, expectedProxyOverride, StringComparison.Ordinal) &&
                            !string.Equals(expectedProxyOverride, originalProxyOverride, StringComparison.Ordinal);

        var unchangedByOthers =
            (current.ProxyEnable == 1 || current.ProxyEnable == original.ProxyEnable) &&
            (string.IsNullOrWhiteSpace(current.AutoConfigUrl) || string.Equals(current.AutoConfigUrl, original.AutoConfigUrl, StringComparison.Ordinal)) &&
            (httpOwned || SameProxy(protocols, originalProtocols, "http")) &&
            (httpsOwned || SameProxy(protocols, originalProtocols, "https")) &&
            OtherProtocolsUnchanged(protocols, originalProtocols) &&
            (string.Equals(proxyOverride, expectedProxyOverride, StringComparison.Ordinal) ||
             string.Equals(proxyOverride, originalProxyOverride, StringComparison.Ordinal));

        return unchangedByOthers && (httpOwned || httpsOwned || overrideOwned);
    }

    /// <summary>Titanium drops non-HTTP entries when it sets the proxy, so each remaining one must still be the snapshot's.</summary>
    private static bool OtherProtocolsUnchanged(IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> original)
    {
        return current.Keys
            .Where(protocol => !protocol.Equals("http", StringComparison.OrdinalIgnoreCase) && !protocol.Equals("https", StringComparison.OrdinalIgnoreCase))
            .All(protocol => SameProxy(current, original, protocol));
    }

    /// <summary>
    ///     Maps each WinINET entry to its protocol. An unnamed <c>host:port</c> entry is kept under the empty key and, as in
    ///     Titanium, also stands for HTTP and HTTPS wherever no named entry sets them.
    /// </summary>
    private static Dictionary<string, string> ParseProxyServer(string? proxyServer)
    {
        var protocols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? unnamed = null;
        foreach (var entry in (proxyServer ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                protocols[parts[0]] = parts[1];
            }
            else
            {
                unnamed = entry;
            }
        }

        if (unnamed is not null)
        {
            protocols[UnnamedProtocol] = unnamed;
            protocols.TryAdd("http", unnamed);
            protocols.TryAdd("https", unnamed);
        }

        return protocols;
    }

    /// <summary>True when both settings hold the same entry for <paramref name="protocol" />.</summary>
    private static bool SameProxy(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> original,
        string protocol)
    {
        return string.Equals(current.GetValueOrDefault(protocol), original.GetValueOrDefault(protocol), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the entry for <paramref name="protocol" /> points at <paramref name="expected" />.</summary>
    private static bool ProxyMatches(IReadOnlyDictionary<string, string> protocols, string protocol, IPEndPoint expected)
    {
        if (!protocols.TryGetValue(protocol, out var value) || !Uri.TryCreate($"http://{value}", UriKind.Absolute, out var uri) ||
            !IPAddress.TryParse(uri.Host, out var address))
        {
            return false;
        }

        return address.Equals(expected.Address) && uri.Port == expected.Port;
    }

    /// <summary>Reads the current WinINET values through Titanium's registry reader.</summary>
    private static WindowsProxyInfo ReadWindowsProxyInfo(object backend)
    {
        var readCurrent = backend.GetType().GetMethod("GetProxyInfoFromRegistry", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The installed Windows proxy backend cannot verify proxy ownership.");
        var current = readCurrent.Invoke(null, null)
            ?? throw new InvalidOperationException("The current Windows proxy settings could not be read.");
        return new WindowsProxyInfo(
            current.GetType().GetProperty("ProxyEnable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(current) as int?,
            current.GetType().GetProperty("AutoConfigUrl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(current) as string,
            current.GetType().GetProperty("ProxyServer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(current) as string,
            current.GetType().GetProperty("ProxyOverride", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(current) as string);
    }

    /// <summary>Reads the WinINET values saved in a snapshot.</summary>
    private static WindowsProxyInfo ReadOriginalWindowsProxyInfo(string snapshot)
    {
        using var document = JsonDocument.Parse(snapshot);
        var value = document.RootElement.GetProperty("values").GetProperty("originalValues");
        using var original = JsonDocument.Parse(value.GetString() ?? "null");
        if (original.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new WindowsProxyInfo(null, null, null, null);
        }

        var root = original.RootElement;
        return new WindowsProxyInfo(
            root.TryGetProperty("ProxyEnable", out var enable) && enable.ValueKind == JsonValueKind.Number ? enable.GetInt32() : null,
            StringProperty(root, "AutoConfigUrl"),
            StringProperty(root, "ProxyServer"),
            StringProperty(root, "ProxyOverride"));
    }

    /// <summary>The string value of <paramref name="property" />, or null when it is missing or not a string.</summary>
    private static string? StringProperty(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>Asks the backend which protocols still point at a local proxy on <paramref name="port" />.</summary>
    private static ProxyProtocolType GetStaleLocalProxyProtocols(object backend, int port)
    {
        var method = backend.GetType().GetMethod("GetStaleLocalProxyProtocols", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("The installed Titanium proxy backend cannot verify proxy ownership.");
        var result = method.Invoke(backend, [new[] { port }]);
        return result is null ? ProxyProtocolType.None : (ProxyProtocolType)Convert.ToInt32(result);
    }

    /// <summary>True when two serialized snapshots hold the same values, ignoring where a switched-off macOS proxy points.</summary>
    internal static bool SnapshotsMatch(string expected, string actual)
    {
        return JsonNode.DeepEquals(Comparable(expected), Comparable(actual));
    }

    /// <summary>
    ///     Parses a snapshot for comparison. macOS restores a switched-off proxy by turning its switch off only, leaving the host and
    ///     port as they were, so those are dropped from every macOS entry whose switch is off.
    /// </summary>
    private static JsonNode? Comparable(string snapshot)
    {
        var root = JsonNode.Parse(snapshot);
        if (root?["backendType"] is not JsonValue type || !type.TryGetValue<string>(out var backendType) ||
            backendType != MacBackendType ||
            root["values"]?["_original"] is not JsonValue original || !original.TryGetValue<string>(out var services) ||
            JsonNode.Parse(services) is not JsonArray entries)
        {
            return root;
        }

        foreach (var entry in entries.OfType<JsonObject>())
        {
            foreach (var protocol in MacProtocols)
            {
                if (entry[$"{protocol}Enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on) && !on)
                {
                    entry.Remove($"{protocol}Host");
                    entry.Remove($"{protocol}Port");
                }
            }
        }

        root["values"]!["_original"] = entries.ToJsonString();
        return root;
    }

    /// <summary>Reads the system settings now in effect through a short-lived server, leaving no snapshot armed in a long-lived one.</summary>
    internal static string CaptureCurrentSettings()
    {
        using var verificationServer = new ProxyServer(false);
        return new TitaniumSystemProxySnapshotProvider(verificationServer).Capture();
    }

    /// <summary>True when at least one protocol still points at the proxy.</summary>
    private static bool HasOwnedProtocols(ProxyProtocolType protocols) => protocols != ProxyProtocolType.None;

    /// <summary>Loads a saved snapshot into this server's backend, so its restore writes those settings.</summary>
    internal void HydrateSnapshot(string snapshot)
    {
        var document = JsonSerializer.Deserialize<SnapshotDocument>(snapshot, JsonOptions)
            ?? throw new InvalidOperationException("The saved system proxy snapshot is empty.");
        if (document.Version != 1)
        {
            throw new InvalidOperationException($"System proxy snapshot version {document.Version} is not supported.");
        }

        var backend = GetBackend(server);
        if (!string.Equals(backend.GetType().FullName, document.BackendType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The saved system proxy snapshot belongs to a different platform backend.");
        }

        foreach (var name in SnapshotFieldNames())
        {
            if (!document.Values.TryGetValue(name, out var json))
            {
                throw new InvalidOperationException($"The saved system proxy snapshot is missing {name}.");
            }

            var field = GetField(backend, name);
            field.SetValue(backend, SystemProxySnapshotCodec.Read(json, field.FieldType));
        }

    }

    /// <summary>The Titanium platform backend behind <paramref name="proxyServer" />.</summary>
    private static object GetBackend(ProxyServer proxyServer)
    {
        var property = typeof(ProxyServer).GetProperty("SystemProxySettingsManager", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The installed Titanium proxy backend is not available.");
        return property.GetValue(proxyServer)
            ?? throw new InvalidOperationException("System proxy settings are not supported on this platform.");
    }

    /// <summary>Makes the backend save the current settings without changing them.</summary>
    private static void CaptureOriginalSettings(object backend)
    {
        if (OperatingSystem.IsWindows())
        {
            var method = backend.GetType().GetMethod("GetProxyInfoFromRegistry", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The installed Windows proxy backend cannot capture its original settings.");
            var original = method.Invoke(null, null);
            if (original is null)
            {
                throw new InvalidOperationException("Windows proxy settings could not be read for crash recovery.");
            }

            GetField(backend, "originalValues").SetValue(backend, original);
            return;
        }

        var ensureSnapshot = backend.GetType().GetMethod("EnsureSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The installed proxy backend cannot capture its original settings.");
        ensureSnapshot.Invoke(backend, null);
    }

    /// <summary>The backend fields that hold this platform's saved settings.</summary>
    private static string[] SnapshotFieldNames()
    {
        if (OperatingSystem.IsWindows())
        {
            return ["originalValues"];
        }

        if (OperatingSystem.IsMacOS())
        {
            return ["_original", "_hasSnapshot"];
        }

        if (OperatingSystem.IsLinux())
        {
            return ["_originalEnv", "_gnome", "_kde", "_hasSnapshot"];
        }

        throw new PlatformNotSupportedException("Crash-safe system proxy restoration is not supported on this platform.");
    }

    /// <summary>Reads a private backend field.</summary>
    private static object? ReadField(object backend, string name) => GetField(backend, name).GetValue(backend);

    /// <summary>Finds a private backend field, failing when Titanium no longer has it.</summary>
    private static FieldInfo GetField(object backend, string name)
    {
        return backend.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"The installed Titanium proxy backend no longer exposes snapshot field {name}.");
    }

    /// <summary>The serialized snapshot: format version, backend type and each field as JSON.</summary>
    private sealed record SnapshotDocument(int Version, string BackendType, Dictionary<string, string> Values);
}

/// <summary>The WinINET proxy values Titanium reads from and restores to the registry.</summary>
internal sealed record WindowsProxyInfo(int? ProxyEnable, string? AutoConfigUrl, string? ProxyServer, string? ProxyOverride);
