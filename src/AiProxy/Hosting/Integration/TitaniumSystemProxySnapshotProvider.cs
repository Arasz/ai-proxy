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
    public Task<bool> RestoreIfOwned(string endpoint, string snapshot)
    {
        if (!IsOwned(endpoint, snapshot))
        {
            return Task.FromResult(false);
        }

        HydrateSnapshot(snapshot);
        if (!IsOwned(endpoint, snapshot))
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
    public bool IsOwned(string endpoint, string snapshot)
    {
        if (!IPEndPoint.TryParse(endpoint, out var endpointAddress))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return UnixProxyIsOwned(GetStaleLocalProxyProtocols(GetBackend(server), endpointAddress.Port));
        }

        var original = ReadOriginalWindowsProxyInfo(snapshot);
        var expectedProxyOverride = TitaniumSystemProxyRegistrar.BypassSettings().BuildProxyOverride(original.ProxyOverride);
        return WindowsProxyIsOwned(ReadWindowsProxyInfo(GetBackend(server)), original, endpointAddress.ToString(), expectedProxyOverride);
    }

    internal static bool WindowsProxyIsActive(int? proxyEnable, string? autoConfigUrl)
    {
        return proxyEnable == 1 && string.IsNullOrWhiteSpace(autoConfigUrl);
    }

    /// <summary>ai-proxy always sets HTTP and HTTPS together, so a single protocol still pointing at it means the other was changed.</summary>
    internal static bool UnixProxyIsOwned(ProxyProtocolType owned) => owned == ProxyProtocolType.AllHttp;

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

    private static Dictionary<string, string> ParseProxyServer(string? proxyServer)
    {
        return (proxyServer ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameProxy(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> original,
        string protocol)
    {
        return string.Equals(current.GetValueOrDefault(protocol), original.GetValueOrDefault(protocol), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProxyMatches(IReadOnlyDictionary<string, string> protocols, string protocol, IPEndPoint expected)
    {
        if (!protocols.TryGetValue(protocol, out var value) || !Uri.TryCreate($"http://{value}", UriKind.Absolute, out var uri) ||
            !IPAddress.TryParse(uri.Host, out var address))
        {
            return false;
        }

        return address.Equals(expected.Address) && uri.Port == expected.Port;
    }

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

    private static string? StringProperty(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static ProxyProtocolType GetStaleLocalProxyProtocols(object backend, int port)
    {
        var method = backend.GetType().GetMethod("GetStaleLocalProxyProtocols", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("The installed Titanium proxy backend cannot verify proxy ownership.");
        var result = method.Invoke(backend, [new[] { port }]);
        return result is null ? ProxyProtocolType.None : (ProxyProtocolType)Convert.ToInt32(result);
    }

    internal static bool SnapshotsMatch(string expected, string actual)
    {
        return JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual));
    }

    private static string CaptureCurrentSettings()
    {
        using var verificationServer = new ProxyServer(false);
        return new TitaniumSystemProxySnapshotProvider(verificationServer).Capture();
    }

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

    private static object GetBackend(ProxyServer proxyServer)
    {
        var property = typeof(ProxyServer).GetProperty("SystemProxySettingsManager", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The installed Titanium proxy backend is not available.");
        return property.GetValue(proxyServer)
            ?? throw new InvalidOperationException("System proxy settings are not supported on this platform.");
    }

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

    private static object? ReadField(object backend, string name) => GetField(backend, name).GetValue(backend);

    private static FieldInfo GetField(object backend, string name)
    {
        return backend.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"The installed Titanium proxy backend no longer exposes snapshot field {name}.");
    }

    private sealed record SnapshotDocument(int Version, string BackendType, Dictionary<string, string> Values);
}

/// <summary>The WinINET proxy values Titanium reads from and restores to the registry.</summary>
internal sealed record WindowsProxyInfo(int? ProxyEnable, string? AutoConfigUrl, string? ProxyServer, string? ProxyOverride);
