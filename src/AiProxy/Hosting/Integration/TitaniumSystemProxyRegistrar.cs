using System.Net;
using AiProxy.Hosting.Proxy;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace AiProxy.Hosting.Integration;

/// <summary>Uses Titanium's platform integrations to change and restore system proxy settings.</summary>
/// <param name="server">The running proxy server whose system proxy settings are changed.</param>
/// <param name="platform">The operating-system side that captures, applies, restores and disarms the settings.</param>
public sealed class TitaniumSystemProxyRegistrar(ProxyServer server, ISystemProxyPlatform platform) : ISystemProxyRegistrar
{
    private string? _previousSettingsSnapshot;
    private string? _endpoint;
    private string? _appliedSettingsSnapshot;

    /// <summary>Changes the real system proxy through <paramref name="server" />'s platform backend.</summary>
    public TitaniumSystemProxyRegistrar(ProxyServer server) : this(server, new TitaniumSystemProxyPlatform(server))
    {
    }

    /// <summary>A snapshot provider over the server's backend, for ownership checks.</summary>
    private TitaniumSystemProxySnapshotProvider SnapshotProvider => new(server);

    /// <inheritdoc />
    public string Capture()
    {
        _previousSettingsSnapshot = platform.Capture();
        return _previousSettingsSnapshot;
    }

    /// <inheritdoc />
    public bool IsOwned(string endpoint) => SnapshotProvider.IsOwned(endpoint);

    /// <inheritdoc />
    public bool IsOwned(string endpoint, string snapshot, string? appliedSnapshot) => SnapshotProvider.IsOwned(endpoint, snapshot, appliedSnapshot);

    /// <inheritdoc />
    public Task<bool> RestoreIfOwned(string endpoint, string snapshot, string? appliedSnapshot) =>
        SnapshotProvider.RestoreIfOwned(endpoint, snapshot, appliedSnapshot);

    /// <inheritdoc />
    public string Set(ExplicitProxyEndPoint endPoint)
    {
        Prepare(endPoint);
        _appliedSettingsSnapshot = platform.Apply(endPoint);
        return _appliedSettingsSnapshot;
    }

    /// <summary>The snapshot captured before the proxy was set, or null before the first capture.</summary>
    internal string? PreviousSettingsSnapshot => _previousSettingsSnapshot;

    /// <summary>Records the endpoint <see cref="Restore" /> checks ownership against, capturing a snapshot when none was taken yet.</summary>
    internal void Prepare(ExplicitProxyEndPoint endPoint)
    {
        _previousSettingsSnapshot ??= platform.Capture();
        _endpoint = new IPEndPoint(endPoint.IpAddress, endPoint.Port).ToString();
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
    public bool Restore()
    {
        if (_endpoint is null || _previousSettingsSnapshot is null)
        {
            throw new InvalidOperationException("The system proxy recovery snapshot is unavailable.");
        }

        if (platform.RestoreIfOwned(_endpoint, _previousSettingsSnapshot, _appliedSettingsSnapshot))
        {
            return true;
        }

        // Someone changed the settings since; Server.Stop and process exit must not restore the stale snapshot over them.
        platform.Disarm();
        return false;
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
