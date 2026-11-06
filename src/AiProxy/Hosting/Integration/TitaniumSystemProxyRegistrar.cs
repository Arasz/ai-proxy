using System.Net;
using AiProxy.Hosting.Proxy;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace AiProxy.Hosting.Integration;

/// <summary>Uses Titanium's platform integrations to change and restore system proxy settings.</summary>
/// <param name="server">The running proxy server whose system proxy settings are changed.</param>
public sealed class TitaniumSystemProxyRegistrar(ProxyServer server) : ISystemProxyRegistrar
{
    private string? _previousSettingsSnapshot;
    private string? _endpoint;

    private TitaniumSystemProxySnapshotProvider SnapshotProvider => new(server);

    /// <inheritdoc />
    public string Capture()
    {
        _previousSettingsSnapshot = SnapshotProvider.Capture();
        return _previousSettingsSnapshot;
    }

    /// <inheritdoc />
    public bool IsOwned(string endpoint) => SnapshotProvider.IsOwned(endpoint);

    /// <inheritdoc />
    public bool IsOwned(string endpoint, string snapshot) => SnapshotProvider.IsOwned(endpoint, snapshot);

    /// <inheritdoc />
    public Task<bool> RestoreIfOwned(string endpoint, string snapshot) => SnapshotProvider.RestoreIfOwned(endpoint, snapshot);

    /// <inheritdoc />
    public void Set(ExplicitProxyEndPoint endPoint)
    {
        _endpoint = new IPEndPoint(endPoint.IpAddress, endPoint.Port).ToString();
        var result = server.TrySetAsSystemProxy(endPoint, ProxyProtocolType.AllHttp, BypassSettings());
        if (!result.Succeeded)
        {
            server.TryRestoreOriginalProxySettings();
            throw new InvalidOperationException($"The system proxy could not be set to ai-proxy: {result.Message}");
        }
    }

    /// <summary>Adds the loopback hosts to the system bypass list, so local calls never loop back into the proxy.</summary>
    public static SystemProxySettings BypassSettings()
    {
        var settings = new SystemProxySettings { BypassRuleMode = SystemProxyBypassRuleMode.Merge };
        foreach (var host in (string[])["localhost", "127.0.0.1", "::1"])
        {
            settings.BypassRules.Add(host);
        }

        return settings;
    }

    /// <inheritdoc />
    public void Restore()
    {
        if (_endpoint is null || _previousSettingsSnapshot is null)
        {
            throw new InvalidOperationException("The system proxy recovery snapshot is unavailable.");
        }

        SnapshotProvider.RestoreIfOwned(_endpoint, _previousSettingsSnapshot).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public Task ClearAsync() => ClearSystemProxy();

    /// <summary>
    ///     Turns the HTTP and HTTPS system proxies off through a short-lived server, so no snapshot lands in a long-lived one.
    ///     Titanium snapshot the settings it first touches and restores them at exit; a snapshot of a stale proxy must not outlive the clear.
    /// </summary>
    public static Task ClearSystemProxy()
    {
        using var server = new ProxyServer(false);
        var result = server.TryDisableSystemProxy(ProxyProtocolType.AllHttp);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"The system proxy could not be cleared: {result.Message}");
        }

        return Task.CompletedTask;
    }
}
