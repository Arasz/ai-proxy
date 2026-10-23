using System.Net;

namespace AiProxy.Hosting;

/// <summary>Controls the local listener and its reversible machine proxy integration.</summary>
public sealed record ProxyRuntimeOptions(
    IPAddress ListenAddress,
    int Port,
    bool ConfigureSystemProxy,
    bool TrustRootCertificate)
{
    /// <summary>The loopback endpoint used by the installed application.</summary>
    public static ProxyRuntimeOptions Default { get; } = new(IPAddress.Loopback, 8080, true, true);
}
