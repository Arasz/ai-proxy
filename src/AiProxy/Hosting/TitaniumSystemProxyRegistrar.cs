using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace AiProxy.Hosting;

/// <summary>Uses Titanium's platform integrations to change and restore system proxy settings.</summary>
public sealed class TitaniumSystemProxyRegistrar : ISystemProxyRegistrar
{
    private readonly ProxyServer _server;

    /// <summary>Creates a registrar for one running proxy server.</summary>
    public TitaniumSystemProxyRegistrar(ProxyServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
    }

    /// <inheritdoc />
    public void Set(ExplicitProxyEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        var result = _server.TrySetAsSystemProxy(endPoint, ProxyProtocolType.AllHttp);
        if (!result.Succeeded)
        {
            _server.TryRestoreOriginalProxySettings();
            throw new InvalidOperationException($"The system proxy could not be set to ai-proxy: {result.Message}");
        }
    }

    /// <inheritdoc />
    public void Restore()
    {
        // The macOS backend swallows some of its own errors, so a success result can still hide a partial restore.
        var result = _server.TryRestoreOriginalProxySettings();
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"The system proxy could not be restored: {result.Message}");
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        ClearSystemProxy();
    }

    /// <summary>
    ///     Turns the HTTP and HTTPS system proxies off through a short-lived server, so no snapshot lands in a long-lived one.
    ///     Titanium snapshots the settings it first touches and restores them at exit; a snapshot of a stale proxy must not outlive the clear.
    /// </summary>
    public static void ClearSystemProxy()
    {
        using var server = new ProxyServer(false);
        var result = server.TryDisableSystemProxy(ProxyProtocolType.AllHttp);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"The system proxy could not be cleared: {result.Message}");
        }
    }
}