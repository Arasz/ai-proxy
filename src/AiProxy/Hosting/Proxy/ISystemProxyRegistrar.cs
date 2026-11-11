using Titanium.Web.Proxy.Models;

namespace AiProxy.Hosting.Proxy;

/// <summary>Installs and restores the user's operating-system proxy settings.</summary>
public interface ISystemProxyRegistrar : ISystemProxySnapshotProvider
{
    /// <summary>
    ///     Sets both HTTP and HTTPS system proxies to an endpoint already added to the running server and returns the settings now applied;
    ///     throws, leaving settings unchanged, when that fails.
    /// </summary>
    string Set(ExplicitProxyEndPoint endPoint);

    /// <summary>Restores the settings observed before the proxy was installed; false when they changed since and were left alone.</summary>
    bool Restore();

    /// <summary>Turns the HTTP and HTTPS system proxies off without needing a running server; throws when that fails.</summary>
    Task ClearAsync();
}
