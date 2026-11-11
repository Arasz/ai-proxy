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
    private string? _appliedSettingsSnapshot;

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
    public bool IsOwned(string endpoint, string snapshot, string? appliedSnapshot) => SnapshotProvider.IsOwned(endpoint, snapshot, appliedSnapshot);

    /// <inheritdoc />
    public Task<bool> RestoreIfOwned(string endpoint, string snapshot, string? appliedSnapshot) =>
        SnapshotProvider.RestoreIfOwned(endpoint, snapshot, appliedSnapshot);

    /// <inheritdoc />
    public string Set(ExplicitProxyEndPoint endPoint)
    {
        Prepare(endPoint);
        var result = server.TrySetAsSystemProxy(endPoint, ProxyProtocolType.AllHttp, BypassSettings());
        if (!result.Succeeded)
        {
            server.TryRestoreOriginalProxySettings();
            throw new InvalidOperationException($"The system proxy could not be set to ai-proxy: {result.Message}");
        }

        try
        {
            // Read back through the same reader restore verification uses, so ownership checks compare like with like.
            _appliedSettingsSnapshot = TitaniumSystemProxySnapshotProvider.CaptureCurrentSettings();
        }
        catch (Exception exception)
        {
            server.TryRestoreOriginalProxySettings();
            throw new InvalidOperationException($"The system proxy was set but could not be read back: {exception.Message}", exception);
        }

        return _appliedSettingsSnapshot;
    }

    internal string? PreviousSettingsSnapshot => _previousSettingsSnapshot;

    /// <summary>Records the endpoint <see cref="Restore" /> checks ownership against, capturing a snapshot when none was taken yet.</summary>
    internal void Prepare(ExplicitProxyEndPoint endPoint)
    {
        _previousSettingsSnapshot ??= SnapshotProvider.Capture();
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

        var provider = SnapshotProvider;
        if (provider.RestoreIfOwned(_endpoint, _previousSettingsSnapshot, _appliedSettingsSnapshot).GetAwaiter().GetResult())
        {
            return true;
        }

        // Someone changed the settings since; Server.Stop and process exit must not restore the stale snapshot over them.
        provider.Disarm();
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
