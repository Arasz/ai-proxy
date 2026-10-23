using System.Net;
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
    public void Set(IPAddress address, int port)
    {
        var endPoint = new ExplicitProxyEndPoint(address, port, decryptSsl: true);
        _server.SetAsSystemHttpProxy(endPoint);
        _server.SetAsSystemHttpsProxy(endPoint);
    }

    /// <inheritdoc />
    public void Restore() => _server.RestoreOriginalProxySettings();
}
