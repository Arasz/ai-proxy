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
        var backend = GetBackend(server);
        CaptureOriginalSettings(backend);
        var fields = SnapshotFieldNames()
            .ToDictionary(name => name, name => ReadField(backend, name), StringComparer.Ordinal);
        var values = fields.ToDictionary(
            pair => pair.Key,
            pair => SystemProxySnapshotCodec.Write(pair.Value, GetField(backend, pair.Key).FieldType),
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(new SnapshotDocument(1, backend.GetType().FullName!, values), JsonOptions);
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
        if (!OperatingSystem.IsWindows())
        {
            return IsOwned(endpoint);
        }

        if (!IPEndPoint.TryParse(endpoint, out var endpointAddress))
        {
            return false;
        }

        var originalProxyOverride = ReadOriginalWindowsProxyOverride(snapshot);
        var originalProxyServer = ReadOriginalWindowsProxyServer(snapshot);
        var expectedProxyOverride = TitaniumSystemProxyRegistrar.BypassSettings().BuildProxyOverride(originalProxyOverride);
        var current = ReadWindowsProxyInfo(GetBackend(server));
        return WindowsProxyIsOwned(
            current.ProxyEnable,
            current.AutoConfigUrl,
            current.ProxyServer,
            current.ProxyOverride,
            originalProxyServer,
            originalProxyOverride,
            endpointAddress.ToString(),
            expectedProxyOverride);
    }

    internal static bool WindowsProxyIsActive(int? proxyEnable, string? autoConfigUrl)
    {
        return proxyEnable == 1 && string.IsNullOrWhiteSpace(autoConfigUrl);
    }

    internal static bool WindowsProxyIsOwned(
        int? proxyEnable,
        string? autoConfigUrl,
        string? proxyServer,
        string? proxyOverride,
        string? originalProxyServer,
        string? originalProxyOverride,
        string endpoint,
        string expectedProxyOverride)
    {
        if (!WindowsProxyIsActive(proxyEnable, autoConfigUrl) || !IPEndPoint.TryParse(endpoint, out var expected))
        {
            return false;
        }

        var protocols = ParseProxyServer(proxyServer);
        var originalProtocols = ParseProxyServer(originalProxyServer);
        var httpOwned = ProxyMatches(protocols, "http", expected);
        var httpsOwned = ProxyMatches(protocols, "https", expected);
        var otherProtocolsMatchSnapshot =
            (httpOwned || SameProxy(protocols, originalProtocols, "http")) &&
            (httpsOwned || SameProxy(protocols, originalProtocols, "https"));

        return (httpOwned || httpsOwned) &&
               otherProtocolsMatchSnapshot &&
               (string.Equals(proxyOverride ?? string.Empty, expectedProxyOverride, StringComparison.Ordinal) ||
                string.Equals(proxyOverride ?? string.Empty, originalProxyOverride ?? string.Empty, StringComparison.Ordinal));
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

    private static (int? ProxyEnable, string? AutoConfigUrl, string? ProxyServer, string? ProxyOverride) ReadWindowsProxyInfo(object backend)
    {
        var readCurrent = backend.GetType().GetMethod("GetProxyInfoFromRegistry", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The installed Windows proxy backend cannot verify proxy ownership.");
        var current = readCurrent.Invoke(null, null)
            ?? throw new InvalidOperationException("The current Windows proxy settings could not be read.");
        return (
            current.GetType().GetProperty("ProxyEnable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(current) as int?,
            current.GetType().GetProperty("AutoConfigUrl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(current) as string,
            current.GetType().GetProperty("ProxyServer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(current) as string,
            current.GetType().GetProperty("ProxyOverride", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(current) as string);
    }

    private static string? ReadOriginalWindowsProxyOverride(string snapshot)
    {
        return ReadOriginalWindowsProxyProperty(snapshot, "ProxyOverride");
    }

    private static string? ReadOriginalWindowsProxyServer(string snapshot)
    {
        return ReadOriginalWindowsProxyProperty(snapshot, "ProxyServer");
    }

    private static string? ReadOriginalWindowsProxyProperty(string snapshot, string property)
    {
        using var document = JsonDocument.Parse(snapshot);
        var value = document.RootElement.GetProperty("values").GetProperty("originalValues");
        using var original = JsonDocument.Parse(value.GetString() ?? "null");
        return original.RootElement.ValueKind == JsonValueKind.Object && original.RootElement.TryGetProperty(property, out var valueNode)
            ? valueNode.GetString()
            : null;
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

    /// <inheritdoc />
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
