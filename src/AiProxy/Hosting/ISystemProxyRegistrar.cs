using System.Net;

namespace AiProxy.Hosting;

/// <summary>Installs and restores the user's operating-system proxy settings.</summary>
public interface ISystemProxyRegistrar
{
    /// <summary>Sets both HTTP and HTTPS system proxies to the local listener.</summary>
    void Set(IPAddress address, int port);

    /// <summary>Restores the settings observed before the proxy was installed.</summary>
    void Restore();
}
