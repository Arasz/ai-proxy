using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AiProxy.Hosting.Integration;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

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

        using (var currentServer = new ProxyServer(false))
        {
            var currentSnapshot = new TitaniumSystemProxySnapshotProvider(currentServer).Capture();
            Assert.True(TitaniumSystemProxySnapshotProvider.SnapshotsMatch(snapshot, currentSnapshot));
        }

        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("backendType").GetString()));
        Assert.NotEmpty(document.RootElement.GetProperty("values").EnumerateObject());
    }

    [Fact]
    public void Hydrate_loads_the_saved_values_into_a_fresh_backend()
    {
        using var server = new ProxyServer(false);
        var snapshot = new TitaniumSystemProxySnapshotProvider(server).Capture();
        using var recoveryServer = new ProxyServer(false);
        var recoveryProvider = new TitaniumSystemProxySnapshotProvider(recoveryServer);
        Assert.False(TitaniumSystemProxySnapshotProvider.SnapshotsMatch(snapshot, recoveryProvider.Read()), "A fresh backend must start without the saved values.");

        recoveryProvider.HydrateSnapshot(snapshot);

        Assert.True(TitaniumSystemProxySnapshotProvider.SnapshotsMatch(snapshot, recoveryProvider.Read()));
        Assert.True(recoveryProvider.IsArmed);
    }

    [Fact]
    public void Disarm_drops_the_backend_snapshot_so_stopping_the_server_restores_nothing()
    {
        using var server = new ProxyServer(false);
        var provider = new TitaniumSystemProxySnapshotProvider(server);
        provider.Capture();
        Assert.True(provider.IsArmed);

        provider.Disarm();

        Assert.False(provider.IsArmed);
    }

    [Fact]
    public void A_restore_after_set_without_capture_skips_a_proxy_it_no_longer_owns_and_disarms_titanium()
    {
        using var server = new ProxyServer(false);
        var platform = new FakePlatform();
        var registrar = new TitaniumSystemProxyRegistrar(server, platform);
        registrar.Set(new ExplicitProxyEndPoint(IPAddress.Loopback, 9090));
        platform.Owned = false;

        var restored = registrar.Restore();

        Assert.False(restored);
        Assert.True(platform.Disarmed);
        Assert.Equal(1, platform.Captures);
        Assert.Null(platform.Restored);
    }

    [Fact]
    public void A_shutdown_restore_checks_ownership_against_the_settings_set_applied()
    {
        using var server = new ProxyServer(false);
        var platform = new FakePlatform();
        var registrar = new TitaniumSystemProxyRegistrar(server, platform);
        registrar.Set(new ExplicitProxyEndPoint(IPAddress.Loopback, 9090));

        var restored = registrar.Restore();

        Assert.True(restored);
        Assert.False(platform.Disarmed);
        Assert.Equal(new RestoreCall("127.0.0.1:9090", "captured-1", "applied-127.0.0.1:9090"), platform.Restored);
    }

    private sealed record RestoreCall(string Endpoint, string Snapshot, string? AppliedSnapshot);

    /// <summary>Stands in for the operating system, so registrar tests never change real proxy settings.</summary>
    private sealed class FakePlatform : ISystemProxyPlatform
    {
        /// <summary>Whether a restore still finds ai-proxy's settings in place.</summary>
        public bool Owned { get; set; } = true;

        /// <summary>How many snapshots were captured.</summary>
        public int Captures { get; private set; }

        /// <summary>True once the saved settings were dropped.</summary>
        public bool Disarmed { get; private set; }

        /// <summary>The arguments of the restore that ran, or null when none did.</summary>
        public RestoreCall? Restored { get; private set; }

        /// <inheritdoc />
        public string Capture() => $"captured-{++Captures}";

        /// <inheritdoc />
        public string Apply(ExplicitProxyEndPoint endPoint) => $"applied-{endPoint.IpAddress}:{endPoint.Port}";

        /// <inheritdoc />
        public bool RestoreIfOwned(string endpoint, string snapshot, string? appliedSnapshot)
        {
            if (!Owned)
            {
                return false;
            }

            Restored = new RestoreCall(endpoint, snapshot, appliedSnapshot);
            return true;
        }

        /// <inheritdoc />
        public void Disarm() => Disarmed = true;
    }

    [Fact]
    public void Prepare_keeps_a_snapshot_captured_before_it()
    {
        using var server = new ProxyServer(false);
        var registrar = new TitaniumSystemProxyRegistrar(server);
        var captured = registrar.Capture();

        registrar.Prepare(new ExplicitProxyEndPoint(IPAddress.Loopback, UnusedPort()));

        Assert.Same(captured, registrar.PreviousSettingsSnapshot);
    }

    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
