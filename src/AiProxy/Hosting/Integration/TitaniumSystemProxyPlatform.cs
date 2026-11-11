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
    public string Apply(ExplicitProxyEndPoint endPoint) =>
        ApplyWith(
            endPoint,
            target => server.TrySetAsSystemProxy(target, ProxyProtocolType.AllHttp, TitaniumSystemProxyRegistrar.BypassSettings()),
            () => server.TryRestoreOriginalProxySettings(),
            TitaniumSystemProxySnapshotProvider.CaptureCurrentSettings);

    /// <summary>Sets the system proxy with <paramref name="trySet" /> and returns <paramref name="readBack" />'s settings; calls <paramref name="restore" /> on any failure.</summary>
    internal static string ApplyWith(
        ExplicitProxyEndPoint endPoint,
        Func<ExplicitProxyEndPoint, SystemProxyChangeResult> trySet,
        Action restore,
        Func<string> readBack)
    {
        var result = trySet(endPoint);
        if (!result.Succeeded)
        {
            restore();
            throw new InvalidOperationException($"The system proxy could not be set to ai-proxy: {result.Message}");
        }

        try
        {
            // Read back through the same reader restore verification uses, so ownership checks compare like with like.
            return readBack();
        }
        catch (Exception exception)
        {
            restore();
            throw new InvalidOperationException($"The system proxy was set but could not be read back: {exception.Message}", exception);
        }
    }

    /// <inheritdoc />
    public bool RestoreIfOwned(string endpoint, string snapshot, string? appliedSnapshot) =>
        new TitaniumSystemProxySnapshotProvider(server).RestoreIfOwned(endpoint, snapshot, appliedSnapshot).GetAwaiter().GetResult();

    /// <inheritdoc />
    public void Load(string snapshot) => new TitaniumSystemProxySnapshotProvider(server).HydrateSnapshot(snapshot);

    /// <inheritdoc />
    public void Disarm() => new TitaniumSystemProxySnapshotProvider(server).Disarm();
}
