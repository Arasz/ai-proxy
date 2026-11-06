using System.Text.Json;
using AiProxy.Hosting.Integration;
using Titanium.Web.Proxy;

namespace AiProxy.Tests.Unit;

public sealed class TitaniumSystemProxySnapshotProviderTests
{
    [Fact]
    public void Capture_reads_the_platform_snapshot_without_setting_or_restoring_system_proxy_values()
    {
        using var server = new ProxyServer(false);
        var provider = new TitaniumSystemProxySnapshotProvider(server);

        var snapshot = provider.Capture();
        using var document = JsonDocument.Parse(snapshot);

        using var recoveryServer = new ProxyServer(false);
        var recoveryProvider = new TitaniumSystemProxySnapshotProvider(recoveryServer);
        recoveryProvider.HydrateSnapshot(snapshot);
        var currentServer = new ProxyServer(false);
        using (currentServer)
        {
            var currentSnapshot = new TitaniumSystemProxySnapshotProvider(currentServer).Capture();
            Assert.True(TitaniumSystemProxySnapshotProvider.SnapshotsMatch(snapshot, currentSnapshot));
        }

        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("backendType").GetString()));
        Assert.NotEmpty(document.RootElement.GetProperty("values").EnumerateObject());
    }

    [Fact]
    public void Restore_verification_requires_the_complete_snapshot_to_match()
    {
        const string expected = "{\"version\":1,\"values\":{\"proxy\":\"prior\",\"bypass\":\"localhost\"}}";
        const string partial = "{\"version\":1,\"values\":{\"proxy\":\"prior\",\"bypass\":\"changed\"}}";

        Assert.True(TitaniumSystemProxySnapshotProvider.SnapshotsMatch(expected, expected));
        Assert.False(TitaniumSystemProxySnapshotProvider.SnapshotsMatch(expected, partial));
    }
}
