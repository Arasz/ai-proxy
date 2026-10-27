using System.Net;
using System.Net.Sockets;
using AiProxy.Ccr;
using AiProxy.Core.Capture;
using AiProxy.Core.Compression;
using AiProxy.Hosting;
using AiProxy.Hosting.Marker;
using Microsoft.Extensions.Logging.Abstractions;
using Titanium.Web.Proxy.Models;

namespace AiProxy.Tests.Integration;

public sealed class TitaniumProxyServiceTests
{
    [Fact]
    public async Task System_proxy_registration_is_reversed_when_the_proxy_stops()
    {
        using var directory = new TemporaryDirectory();
        var port = FreePort();
        var registrar = new RecordingSystemProxyRegistrar();
        await using var service = CreateService(registrar, port, directory.Path);

        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(registrar.EndPoint);
        Assert.Equal(port, registrar.EndPoint.Port);
        Assert.Equal(IPAddress.Loopback, registrar.EndPoint.IpAddress);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, registrar.RestoreCount);
    }

    [Fact]
    public async Task System_proxy_is_set_to_the_endpoint_registered_with_the_server()
    {
        using var directory = new TemporaryDirectory();
        var registrar = new RecordingSystemProxyRegistrar();
        await using var service = CreateService(registrar, FreePort(), directory.Path);

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Contains(service.EndPoints, endPoint => ReferenceEquals(endPoint, registrar.EndPoint));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_failed_system_proxy_set_is_not_restored_and_stops_the_listener()
    {
        using var directory = new TemporaryDirectory();
        var port = FreePort();
        var registrar = new RecordingSystemProxyRegistrar { FailSet = true };
        await using var service = CreateService(registrar, port, directory.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(TestContext.Current.CancellationToken));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, registrar.RestoreCount);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }

    [Fact]
    public async Task A_stale_marker_clears_the_system_proxy_before_it_is_set()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(MarkerPath(directory), "127.0.0.1:9", TestContext.Current.CancellationToken);
        var events = new List<string>();
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), FreePort(), directory.Path, new RecordingGuardLauncher(events));

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["clear", "set", "launch", "restore", "restored"], events);
    }

    [Fact]
    public async Task A_start_without_a_marker_does_not_clear_and_holds_the_marker()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), FreePort(), directory.Path, new RecordingGuardLauncher(events));

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("clear", events);
        Assert.Contains("set", events);
        Assert.Throws<IOException>(() => new FileStream(MarkerPath(directory), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_marker_held_by_another_process_fails_startup_without_touching_the_system_proxy()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        using var holder = new FileStream(MarkerPath(directory), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var events = new List<string>();
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), FreePort(), directory.Path, new RecordingGuardLauncher(events), TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(TestContext.Current.CancellationToken));

        Assert.Empty(events);
    }

    [Fact]
    public async Task A_clean_stop_restores_then_tells_the_guard_and_removes_the_marker()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), FreePort(), directory.Path, new RecordingGuardLauncher(events));
        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(MarkerPath(directory)));

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["set", "launch", "restore", "restored"], events);
        Assert.False(File.Exists(MarkerPath(directory)));
    }

    [Fact]
    public async Task A_guard_that_cannot_start_restores_the_proxy_releases_the_marker_and_fails_startup()
    {
        using var directory = new TemporaryDirectory();
        var port = FreePort();
        var events = new List<string>();
        await using var service = CreateService(
            new RecordingSystemProxyRegistrar(events),
            port,
            directory.Path,
            new RecordingGuardLauncher(events) { Fail = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["set", "launch", "restore"], events);
        Assert.False(File.Exists(MarkerPath(directory)));
        using var reopened = new FileStream(MarkerPath(directory), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        new TcpListener(IPAddress.Loopback, port).Start();
    }

    [Fact]
    public async Task Without_system_proxy_configuration_there_is_no_marker_guard_or_registrar_call()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        await using var service = new TitaniumProxyService(
            new RecordingSystemProxyRegistrar(events),
            new ProxyRuntimeOptions(IPAddress.Loopback, FreePort(), false, false, directory.Path),
            new ProviderRequestInspector(new PassThroughCompressionPipeline(), new CanonicalProviderHosts(), new NoRequestCapture(), new CcrOff(), NullLogger<ProviderRequestInspector>.Instance, TimeProvider.System, new NoGainLedger()),
            new CanonicalProviderHosts(),
            new RecordingGuardLauncher(events),
            NullLoggerFactory.Instance,
            TimeSpan.Zero);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(events);
        Assert.False(File.Exists(MarkerPath(directory)));
    }

    [Fact]
    public async Task A_guard_that_died_early_does_not_stop_a_clean_stop()
    {
        using var directory = new TemporaryDirectory();
        var port = FreePort();
        var events = new List<string>();
        var launcher = new RecordingGuardLauncher(events) { ReportThrows = true };
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), port, directory.Path, launcher);
        await service.StartAsync(TestContext.Current.CancellationToken);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["set", "launch", "restore", "restored"], events);
        Assert.Equal(1, launcher.DisposeCount);
        Assert.False(File.Exists(MarkerPath(directory)));
        new TcpListener(IPAddress.Loopback, port).Start();
    }

    [Fact]
    public async Task A_restore_that_fails_keeps_the_marker_and_does_not_tell_the_guard()
    {
        using var directory = new TemporaryDirectory();
        var port = FreePort();
        var events = new List<string>();
        var launcher = new RecordingGuardLauncher(events);
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events) { FailRestore = true }, port, directory.Path, launcher);
        await service.StartAsync(TestContext.Current.CancellationToken);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["set", "launch", "restore"], events);
        Assert.Equal(1, launcher.DisposeCount);
        Assert.True(new FileInfo(MarkerPath(directory)).Length > 0);
        new TcpListener(IPAddress.Loopback, port).Start();
    }

    [Fact]
    public async Task The_marker_is_emptied_before_the_guard_is_told_the_proxy_was_restored()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        var launcher = new RecordingGuardLauncher(events);
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), FreePort(), directory.Path, launcher);
        await service.StartAsync(TestContext.Current.CancellationToken);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, launcher.MarkerLengthWhenRestoredWasReported);
    }

    [Fact(Skip = TimingDependent.Reason)]
    public async Task A_marker_held_briefly_by_a_dying_owner_does_not_fail_startup()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var holder = new FileStream(MarkerPath(directory), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            holder.Dispose();
        }, TestContext.Current.CancellationToken);
        var events = new List<string>();
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), FreePort(), directory.Path, new RecordingGuardLauncher(events), TimeSpan.FromSeconds(10));

        await service.StartAsync(TestContext.Current.CancellationToken);

        await release;
        Assert.Contains("set", events);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_port_already_in_use_fails_startup_before_the_system_proxy_or_marker_is_touched()
    {
        using var directory = new TemporaryDirectory();
        var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        try
        {
            var port = ((IPEndPoint)occupier.LocalEndpoint).Port;
            var events = new List<string>();
            await using var service = CreateService(new RecordingSystemProxyRegistrar(events), port, directory.Path, new RecordingGuardLauncher(events));

            await Assert.ThrowsAnyAsync<Exception>(() => service.StartAsync(TestContext.Current.CancellationToken));

            Assert.Empty(events);
            Assert.True(!File.Exists(MarkerPath(directory)) || new FileInfo(MarkerPath(directory)).Length == 0);
            using var reopened = new FileStream(MarkerPath(directory), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            occupier.Stop();
        }
    }

    private static string MarkerPath(TemporaryDirectory directory)
    {
        return Path.Combine(directory.Path, "system-proxy.lock");
    }

    private static TitaniumProxyService CreateService(
        ISystemProxyRegistrar registrar,
        int port,
        string dataDirectory,
        ISystemProxyGuardLauncher? launcher = null,
        TimeSpan? markerWait = null)
    {
        return new TitaniumProxyService(
            registrar,
            new ProxyRuntimeOptions(IPAddress.Loopback, port, true, false, dataDirectory),
            new ProviderRequestInspector(new PassThroughCompressionPipeline(), new CanonicalProviderHosts(), new NoRequestCapture(), new CcrOff(), NullLogger<ProviderRequestInspector>.Instance, TimeProvider.System, new NoGainLedger()),
            new CanonicalProviderHosts(),
            launcher ?? new RecordingGuardLauncher([]),
            NullLoggerFactory.Instance,
            markerWait ?? TimeSpan.FromSeconds(1));
    }

    private static int FreePort()
    {
        var portListener = new TcpListener(IPAddress.Loopback, 0);
        portListener.Start();
        var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();
        return port;
    }

    private sealed class RecordingSystemProxyRegistrar(List<string>? events = null) : ISystemProxyRegistrar
    {
        private readonly List<string>? _events = events;
        public bool FailSet { get; init; }

        public bool FailRestore { get; init; }

        public ExplicitProxyEndPoint? EndPoint { get; private set; }

        public int RestoreCount { get; private set; }

        public void Set(ExplicitProxyEndPoint endPoint)
        {
            if (FailSet)
            {
                throw new InvalidOperationException("networksetup failed");
            }

            _events?.Add("set");
            EndPoint = endPoint;
        }

        public void Restore()
        {
            _events?.Add("restore");
            RestoreCount++;
            if (FailRestore)
            {
                throw new InvalidOperationException("networksetup failed");
            }
        }

        public void Clear()
        {
            _events?.Add("clear");
        }
    }

    private sealed class RecordingGuardLauncher(List<string> events) : ISystemProxyGuardLauncher
    {
        private readonly List<string> _events = events;
        private string _markerPath = "";

        public bool Fail { get; init; }

        public bool ReportThrows { get; init; }

        public int DisposeCount { get; private set; }

        public long MarkerLengthWhenRestoredWasReported { get; private set; } = -1;

        public ISystemProxyGuardSession Launch(string dataDirectory, Action guardExited)
        {
            _events.Add("launch");
            if (Fail)
            {
                throw new InvalidOperationException("cannot start the guard");
            }

            _markerPath = Path.Combine(dataDirectory, SystemProxyMarker.FileName);
            return new Session(this);
        }

        private sealed class Session(RecordingGuardLauncher owner) : ISystemProxyGuardSession
        {
            public void ReportRestored()
            {
                owner._events.Add("restored");
                owner.MarkerLengthWhenRestoredWasReported = new FileInfo(owner._markerPath).Length;
                if (owner.ReportThrows)
                {
                    throw new IOException("Broken pipe");
                }
            }

            public void Dispose()
            {
                owner.DisposeCount++;
            }
        }
    }
}