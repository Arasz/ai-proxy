using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace AiProxy.Hosting.Integration;

/// <summary>Changes the real system proxy through Titanium's platform backend of <paramref name="server" />.</summary>
/// <param name="server">The running proxy server whose backend holds the saved settings.</param>
public sealed class TitaniumSystemProxyPlatform(ProxyServer server) : ISystemProxyPlatform
{
    /// <inheritdoc />
    public string Capture() => new TitaniumSystemProxySnapshotProvider(server).Capture();

    /// <inheritdoc />
    public string Apply(ExplicitProxyEndPoint endPoint)
    {
        var result = server.TrySetAsSystemProxy(endPoint, ProxyProtocolType.AllHttp, TitaniumSystemProxyRegistrar.BypassSettings());
        if (!result.Succeeded)
        {
            server.TryRestoreOriginalProxySettings();
            throw new InvalidOperationException($"The system proxy could not be set to ai-proxy: {result.Message}");
        }

        try
        {
            // Read back through the same reader restore verification uses, so ownership checks compare like with like.
            return TitaniumSystemProxySnapshotProvider.CaptureCurrentSettings();
        }
        catch (Exception exception)
        {
            server.TryRestoreOriginalProxySettings();
            throw new InvalidOperationException($"The system proxy was set but could not be read back: {exception.Message}", exception);
        }
    }

    /// <inheritdoc />
    public bool RestoreIfOwned(string endpoint, string snapshot, string? appliedSnapshot) =>
        new TitaniumSystemProxySnapshotProvider(server).RestoreIfOwned(endpoint, snapshot, appliedSnapshot).GetAwaiter().GetResult();

    /// <inheritdoc />
    public void Disarm() => new TitaniumSystemProxySnapshotProvider(server).Disarm();
}
