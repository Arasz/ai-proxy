using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Hosting.Integration;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;
using static AiProxy.Tests.Unit.ProxySnapshots;

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

    [Fact]
    public void A_replaced_capture_is_loaded_into_the_platform_and_is_what_the_shutdown_restore_puts_back()
    {
        using var server = new ProxyServer(false);
        var captured = Mac(MacService("Wi-Fi", MacProxy.On("corp.example", 80), MacProxy.On("127.0.0.1", 9090)));
        var platform = new FakePlatform { CaptureResult = captured };
        var registrar = new TitaniumSystemProxyRegistrar(server, platform);

        var target = registrar.CaptureRestoreTarget([new IPEndPoint(IPAddress.Loopback, 9090)], null);
        registrar.Set(new ExplicitProxyEndPoint(IPAddress.Loopback, 9090));
        registrar.Restore();

        Assert.True(target.ReplacedStaleValues);
        Assert.NotEqual(captured, target.Snapshot);
        Assert.Equal([target.Snapshot], platform.Loaded);
        Assert.Equal(target.Snapshot, platform.Restored?.Snapshot);
    }

    [Fact]
    public void A_capture_with_nothing_stale_is_kept_and_not_loaded()
    {
        using var server = new ProxyServer(false);
        var captured = Mac(MacService("Wi-Fi", MacProxy.On("corp.example", 80), MacProxy.On("secure.example", 443)));
        var platform = new FakePlatform { CaptureResult = captured };
        var registrar = new TitaniumSystemProxyRegistrar(server, platform);

        var target = registrar.CaptureRestoreTarget([new IPEndPoint(IPAddress.Loopback, 9090)], null);

        Assert.False(target.ReplacedStaleValues);
        Assert.Null(target.CleanFailure);
        Assert.Null(target.LoadFailure);
        Assert.Equal(captured, target.Snapshot);
        Assert.Empty(platform.Loaded);
        Assert.Equal(captured, registrar.PreviousSettingsSnapshot);
    }

    [Fact]
    public void A_capture_that_cannot_be_cleaned_is_kept_and_not_loaded_and_says_why()
    {
        using var server = new ProxyServer(false);
        var platform = new FakePlatform { CaptureResult = "not a snapshot" };
        var registrar = new TitaniumSystemProxyRegistrar(server, platform);

        var target = registrar.CaptureRestoreTarget([new IPEndPoint(IPAddress.Loopback, 9090)], null);

        Assert.NotNull(target.CleanFailure);
        Assert.False(target.ReplacedStaleValues);
        Assert.Equal("not a snapshot", target.Snapshot);
        Assert.Empty(platform.Loaded);
        Assert.Equal("not a snapshot", registrar.PreviousSettingsSnapshot);
    }

    [Fact]
    public void A_cleaned_capture_the_backend_cannot_load_falls_back_to_a_fresh_raw_capture_and_says_why()
    {
        using var server = new ProxyServer(false);
        var captured = Mac(MacService("Wi-Fi", MacProxy.On("corp.example", 80), MacProxy.On("127.0.0.1", 9090)));
        var failure = new MissingFieldException("MacOsSystemProxyBackend", "_original");
        var platform = new FakePlatform { CaptureResult = captured, LoadFailure = failure };
        var registrar = new TitaniumSystemProxyRegistrar(server, platform);

        var target = registrar.CaptureRestoreTarget([new IPEndPoint(IPAddress.Loopback, 9090)], null);

        Assert.Same(failure, target.LoadFailure);
        Assert.False(target.ReplacedStaleValues);
        Assert.Equal(captured, target.Snapshot);
        Assert.True(platform.Disarmed);
        Assert.Equal(2, platform.Captures);
        Assert.Equal(captured, registrar.PreviousSettingsSnapshot);
    }

    [Fact]
    public void Restore_verification_ignores_where_a_switched_off_mac_proxy_points()
    {
        var expected = Mac(MacService("Wi-Fi", MacProxy.On("corp.example", 80), new MacProxy(false, "127.0.0.1", 9)));
        var restored = Mac(MacService("Wi-Fi", MacProxy.On("corp.example", 80), new MacProxy(false, "127.0.0.1", 8080)));

        Assert.True(TitaniumSystemProxySnapshotProvider.SnapshotsMatch(expected, restored));
    }

    [Fact]
    public void Restore_verification_still_compares_where_a_switched_on_mac_proxy_points()
    {
        var expected = Mac(MacService("Wi-Fi", MacProxy.On("corp.example", 80), MacProxy.On("secure.example", 443)));
        var restored = Mac(MacService("Wi-Fi", MacProxy.On("corp.example", 80), MacProxy.On("other.example", 443)));

        Assert.False(TitaniumSystemProxySnapshotProvider.SnapshotsMatch(expected, restored));
    }

    [Fact]
    public void A_cleaned_mac_target_matches_what_a_mac_restore_reads_back()
    {
        var own = MacProxy.On("127.0.0.1", 8080);
        var current = Mac(MacService("Wi-Fi", MacProxy.On("corp.example", 80), MacProxy.On("127.0.0.1", 9)));
        var applied = Mac(MacService("Wi-Fi", own, own));
        var cleaned = StaleProxyValues.Replace(current, [new IPEndPoint(IPAddress.Loopback, 8080), new IPEndPoint(IPAddress.Loopback, 9)], null).Snapshot;

        Assert.True(TitaniumSystemProxySnapshotProvider.SnapshotsMatch(cleaned, MacRestoreReadBack(cleaned, applied)));
    }

    /// <summary>
    ///     What macOS holds after Titanium restores <paramref name="target" /> over <paramref name="applied" />: an enabled proxy is
    ///     written in full, a disabled one only has its switch turned off and keeps the applied host and port.
    /// </summary>
    private static string MacRestoreReadBack(string target, string applied)
    {
        var services = Field(target, "_original")!.AsArray();
        var appliedServices = Field(applied, "_original")!.AsArray();
        foreach (var service in services.OfType<JsonObject>())
        {
            var before = appliedServices.Single(item => item!["Service"]!.GetValue<string>() == service["Service"]!.GetValue<string>())!;
            foreach (var protocol in (string[])["Http", "Https"])
            {
                if (!service[$"{protocol}Enabled"]!.GetValue<bool>())
                {
                    service[$"{protocol}Host"] = before[$"{protocol}Host"]!.GetValue<string>();
                    service[$"{protocol}Port"] = before[$"{protocol}Port"]!.GetValue<int>();
                }
            }
        }

        var root = JsonNode.Parse(target)!;
        root["values"]!["_original"] = services.ToJsonString();
        return root.ToJsonString();
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

        /// <summary>The snapshot every capture returns, or null for a numbered placeholder.</summary>
        public string? CaptureResult { get; init; }

        /// <summary>The snapshots loaded into the backend, in order.</summary>
        public List<string> Loaded { get; } = [];

        /// <summary>What every load throws, or null when loads succeed.</summary>
        public Exception? LoadFailure { get; init; }

        /// <inheritdoc />
        public string Capture()
        {
            Captures++;
            return CaptureResult ?? $"captured-{Captures}";
        }

        /// <inheritdoc />
        public void Load(string snapshot)
        {
            if (LoadFailure is not null)
            {
                throw LoadFailure;
            }

            Loaded.Add(snapshot);
        }

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
