using System.Net;
using AiProxy.Hosting.Guard;
using AiProxy.Hosting.Marker;
using AiProxy.Hosting.Proxy;
using AiProxy.Tests.Integration;

namespace AiProxy.Tests.Unit;

public sealed class SystemProxyRecoveryTests
{
    [Fact]
    public async Task Crash_recovery_restores_the_exact_snapshot_when_the_proxy_still_owns_current_settings()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        const string previous = "before: exact bytes";
        const string owned = "127.0.0.1:9090";
        using (var marker = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System))
        {
            marker.Write(new IPEndPoint(IPAddress.Loopback, 9090), previous);
        }

        var snapshotProvider = new StubSnapshotProvider { Current = owned };
        var recovery = new SystemProxyRecovery(snapshotProvider);
        using var staleMarker = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System);

        var didRestore = await recovery.RestoreIfOwned(staleMarker);

        Assert.True(didRestore);
        Assert.Equal(previous, snapshotProvider.Restored);
        Assert.Equal(previous, snapshotProvider.Current);
    }

    [Fact]
    public async Task Crash_recovery_leaves_settings_alone_after_an_external_change()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        using (var marker = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System))
        {
            marker.Write(new IPEndPoint(IPAddress.Loopback, 9090), "before");
        }

        var snapshotProvider = new StubSnapshotProvider { Current = "changed by the user" };
        var recovery = new SystemProxyRecovery(snapshotProvider);
        using var staleMarker = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System);

        var didRestore = await recovery.RestoreIfOwned(staleMarker);

        Assert.False(didRestore);
        Assert.Equal(0, snapshotProvider.RestoreCount);
    }

    [Fact]
    public async Task Crash_recovery_hands_the_provider_the_settings_the_crashed_run_applied()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        using (var marker = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System))
        {
            marker.Write(new IPEndPoint(IPAddress.Loopback, 9090), "before");
            marker.RecordApplied("after");
        }

        var snapshotProvider = new StubSnapshotProvider { Current = "127.0.0.1:9090" };
        using var staleMarker = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System);

        await new SystemProxyRecovery(snapshotProvider).RestoreIfOwned(staleMarker);

        Assert.Equal("after", snapshotProvider.Applied);
    }

    private sealed class StubSnapshotProvider : ISystemProxySnapshotProvider
    {
        public string? Applied { get; private set; }

        public string Current { get; set; } = "";

        public string? Restored { get; private set; }

        public int RestoreCount { get; private set; }

        public string Capture() => Current;

        public bool IsOwned(string endpoint) => string.Equals(Current, endpoint, StringComparison.Ordinal);

        public bool IsOwned(string endpoint, string snapshot, string? appliedSnapshot) => IsOwned(endpoint);

        public Task<bool> RestoreIfOwned(string endpoint, string snapshot, string? appliedSnapshot)
        {
            Applied = appliedSnapshot;
            if (!string.Equals(Current, endpoint, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            RestoreCount++;
            Restored = snapshot;
            Current = snapshot;
            return Task.FromResult(true);
        }
    }
}
