using AiProxy.Hosting.Proxy;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace AiProxy.Hosting.Integration;

/// <summary>Uses Titanium's platform integrations to change and restore system proxy settings.</summary>
/// <param name="server">The running proxy server whose system proxy settings are changed.</param>
public sealed class TitaniumSystemProxyRegistrar(ProxyServer server) : ISystemProxyRegistrar
{
    /// <inheritdoc />
    public void Set(ExplicitProxyEndPoint endPoint)
    {
        var result = server.TrySetAsSystemProxy(endPoint, ProxyProtocolType.AllHttp);
        if (!result.Succeeded)
        {
            server.TryRestoreOriginalProxySettings();
            throw new InvalidOperationException($"The system proxy could not be set to ai-proxy: {result.Message}");
        }
    }

    /// <inheritdoc />
    public void Restore()
    {
        // The macOS backend swallows some of its own errors, so a success result can still hide a partial restore.
        var result = server.TryRestoreOriginalProxySettings();
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"The system proxy could not be restored: {result.Message}");
        }
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
