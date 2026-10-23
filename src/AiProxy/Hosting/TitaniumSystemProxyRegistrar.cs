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
    public void Restore() => _server.RestoreOriginalProxySettings();
}
