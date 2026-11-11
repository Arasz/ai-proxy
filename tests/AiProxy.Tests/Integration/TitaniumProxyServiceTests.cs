using System.Net;
using System.Net.Sockets;
using AiProxy.Ccr;
using AiProxy.Core.Capture;
using AiProxy.Core.Compression;
using AiProxy.Hosting;
using AiProxy.Hosting.Guard;
using AiProxy.Hosting.Marker;
using AiProxy.Hosting.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Titanium.Web.Proxy.Models;
using ProviderRequestInspector = AiProxy.Hosting.Integration.ProviderRequestInspector;
using TitaniumProxyService = AiProxy.Hosting.Integration.TitaniumProxyService;

namespace AiProxy.Tests.Integration;

public sealed class TitaniumProxyServiceTests
{
    [Fact]
    public async Task System_proxy_registration_is_reversed_when_the_proxy_stops()
    {
        using var directory = new TemporaryDirectory();
        var registrar = new RecordingSystemProxyRegistrar();
        await using var service = CreateService(registrar, 0, directory.Path);

        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(registrar.EndPoint);
        Assert.NotEqual(0, registrar.EndPoint.Port);
        Assert.Equal(service.Address.Port, registrar.EndPoint.Port);
        Assert.Equal(IPAddress.Loopback, registrar.EndPoint.IpAddress);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, registrar.RestoreCount);
    }

    [Fact]
    public async Task System_proxy_is_set_to_the_endpoint_registered_with_the_server()
    {
        using var directory = new TemporaryDirectory();
        var registrar = new RecordingSystemProxyRegistrar();
        await using var service = CreateService(registrar, 0, directory.Path);

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Contains(service.EndPoints, endPoint => ReferenceEquals(endPoint, registrar.EndPoint));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_failed_system_proxy_set_is_not_restored_and_stops_the_listener()
    {
        using var directory = new TemporaryDirectory();
        var registrar = new RecordingSystemProxyRegistrar { FailSet = true };
        await using var service = CreateService(registrar, 0, directory.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(TestContext.Current.CancellationToken));
        await service.StopAsync(CancellationToken.None);
        var port = service.Address.Port;
        Assert.NotEqual(0, port);

        Assert.Equal(0, registrar.RestoreCount);
        Assert.True(File.Exists(MarkerPath(directory)), "A failed system-proxy set must retain its pre-set snapshot for recovery.");
        using var marker = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System);
        Assert.True(marker.PreviousRunDied);
        Assert.Equal("prior-system-proxy-settings", marker.PreviousSettingsSnapshot);
        Assert.False(service.Server.ProxyRunning, "The listener must be released.");
    }

    [Fact]
    public async Task A_stale_marker_clears_the_system_proxy_before_it_is_set()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(MarkerPath(directory), "127.0.0.1:9", TestContext.Current.CancellationToken);
        var events = new List<string>();
        var logs = new RecordingLoggerProvider();
        var registrar = new RecordingSystemProxyRegistrar(events) { OwnedEndpoint = "127.0.0.1:9" };
        await using var service = CreateService(registrar, 0, directory.Path, new RecordingGuardLauncher(events), loggerProvider: logs);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["clear", "capture", "set", "launch", "restore", "restored"], events);
        Assert.Contains(logs.Entries, entry => entry.EventId == 1314);
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId is 1312 or 1313);
    }

    [Fact]
    public async Task A_stale_endpoint_only_marker_keeps_a_proxy_set_since_and_restores_it_on_shutdown()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(MarkerPath(directory), "127.0.0.1:9", TestContext.Current.CancellationToken);
        var events = new List<string>();
        var logs = new RecordingLoggerProvider();
        var registrar = new RecordingSystemProxyRegistrar(events) { OwnedEndpoint = "corporate.example:8080" };
        await using var service = CreateService(registrar, 0, directory.Path, new RecordingGuardLauncher(events), loggerProvider: logs);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["capture", "set", "launch", "restore", "restored"], events);
        Assert.Contains(logs.Entries, entry => entry.EventId == 1313);
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId == 1314);
    }

    [Fact]
    public async Task A_crashed_owner_snapshot_is_left_alone_and_reported_when_the_settings_changed_since()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        using (var marker = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System))
        {
            marker.Write(new IPEndPoint(IPAddress.Loopback, 9090), "exact-prior-settings");
        }

        var events = new List<string>();
        var logs = new RecordingLoggerProvider();
        var registrar = new RecordingSystemProxyRegistrar(events) { OwnedEndpoint = "corporate.example:8080" };
        await using var service = CreateService(registrar, 0, directory.Path, new RecordingGuardLauncher(events), loggerProvider: logs);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["capture", "set", "launch", "restore", "restored"], events);
        Assert.Contains(logs.Entries, entry => entry.EventId == 1313);
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId is 1312 or 1314);
    }

    [Fact]
    public async Task A_left_alone_crashed_snapshot_hands_both_endpoints_to_the_capture_and_saves_the_cleaned_target()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        using (var marker = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System))
        {
            marker.Write(new IPEndPoint(IPAddress.Loopback, 9090), "exact-prior-settings");
        }

        var logs = new RecordingLoggerProvider();
        var dead = new IPEndPoint(IPAddress.Loopback, 9090);
        var registrar = new RecordingSystemProxyRegistrar
        {
            OwnedEndpoint = "corporate.example:8080",
            FailRestore = true,
            StaleValuesAt = [dead]
        };
        await using var service = CreateService(registrar, 0, directory.Path, loggerProvider: logs);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal([dead, new IPEndPoint(IPAddress.Loopback, service.Address.Port)], registrar.TargetRequest?.StaleEndpoints);
        Assert.Equal("exact-prior-settings", registrar.TargetRequest?.PreviousSnapshot);
        Assert.Contains(logs.Entries, entry => entry.EventId == 1313);
        var replaced = Assert.Single(logs.Entries, entry => entry.EventId == 1317);
        Assert.Contains("127.0.0.1:9090", replaced.Message);
        Assert.DoesNotContain($"127.0.0.1:{service.Address.Port}", replaced.Message);
        using var reopened = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System);
        Assert.Equal("cleaned-settings", reopened.PreviousSettingsSnapshot);
    }

    [Fact]
    public async Task A_start_without_a_dead_run_checks_only_its_own_endpoint_and_reports_nothing_when_nothing_was_replaced()
    {
        using var directory = new TemporaryDirectory();
        var logs = new RecordingLoggerProvider();
        var registrar = new RecordingSystemProxyRegistrar();
        await using var service = CreateService(registrar, 0, directory.Path, loggerProvider: logs);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal([new IPEndPoint(IPAddress.Loopback, service.Address.Port)], registrar.TargetRequest?.StaleEndpoints);
        Assert.Null(registrar.TargetRequest?.PreviousSnapshot);
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId is 1317 or 1318 or 1319);
    }

    [Fact]
    public async Task A_capture_that_could_not_be_cleaned_is_reported_and_saved_as_captured()
    {
        using var directory = new TemporaryDirectory();
        var logs = new RecordingLoggerProvider();
        var failure = new InvalidOperationException("The captured system proxy snapshot has an unexpected shape.");
        var registrar = new RecordingSystemProxyRegistrar { FailRestore = true, CleanFailure = failure };
        await using var service = CreateService(registrar, 0, directory.Path, loggerProvider: logs);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        var warning = Assert.Single(logs.Entries, entry => entry.EventId == 1318);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Same(failure, warning.Exception);
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId is 1317 or 1319);
        using var reopened = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System);
        Assert.Equal("prior-system-proxy-settings", reopened.PreviousSettingsSnapshot);
    }

    [Fact]
    public async Task A_cleaned_capture_the_backend_could_not_load_is_reported_and_the_raw_capture_is_saved()
    {
        using var directory = new TemporaryDirectory();
        var logs = new RecordingLoggerProvider();
        var failure = new MissingFieldException("MacOsSystemProxyBackend", "_original");
        var registrar = new RecordingSystemProxyRegistrar { FailRestore = true, LoadFailure = failure };
        await using var service = CreateService(registrar, 0, directory.Path, loggerProvider: logs);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        var warning = Assert.Single(logs.Entries, entry => entry.EventId == 1319);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Same(failure, warning.Exception);
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId is 1317 or 1318);
        using var reopened = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System);
        Assert.Equal("prior-system-proxy-settings", reopened.PreviousSettingsSnapshot);
    }

    [Fact]
    public async Task An_endpoint_only_marker_left_alone_still_has_its_endpoint_checked_for_stale_values()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(MarkerPath(directory), "127.0.0.1:9", TestContext.Current.CancellationToken);
        var registrar = new RecordingSystemProxyRegistrar { OwnedEndpoint = "corporate.example:8080" };
        await using var service = CreateService(registrar, 0, directory.Path);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal([new IPEndPoint(IPAddress.Loopback, 9), new IPEndPoint(IPAddress.Loopback, service.Address.Port)], registrar.TargetRequest?.StaleEndpoints);
        Assert.Null(registrar.TargetRequest?.PreviousSnapshot);
    }

    [Fact]
    public async Task A_shutdown_that_finds_the_settings_changed_reports_the_skipped_restore()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        var logs = new RecordingLoggerProvider();
        var registrar = new RecordingSystemProxyRegistrar(events) { RestoreFindsOwnership = false };
        await using var service = CreateService(registrar, 0, directory.Path, new RecordingGuardLauncher(events), loggerProvider: logs);
        await service.StartAsync(TestContext.Current.CancellationToken);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["capture", "set", "launch", "restore", "restored"], events);
        Assert.Contains(logs.Entries, entry => entry.EventId == 1315);
        Assert.False(File.Exists(MarkerPath(directory)));
    }

    [Fact]
    public async Task A_torn_marker_from_an_interrupted_write_is_reported_and_does_not_block_startup()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(MarkerPath(directory), "{\"endpoint\":\"127.0.0.1:9090\"", TestContext.Current.CancellationToken);
        var events = new List<string>();
        var logs = new RecordingLoggerProvider();
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), 0, directory.Path, new RecordingGuardLauncher(events), loggerProvider: logs);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["capture", "set", "launch", "restore", "restored"], events);
        Assert.Contains(logs.Entries, entry => entry.EventId == 1316);
    }

    [Fact]
    public async Task A_crashed_owner_snapshot_is_restored_before_the_new_proxy_captures_settings()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        using (var marker = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System))
        {
            marker.Write(new IPEndPoint(IPAddress.Loopback, 9090), "exact-prior-settings");
        }

        var events = new List<string>();
        var logs = new RecordingLoggerProvider();
        var registrar = new RecordingSystemProxyRegistrar(events) { OwnedEndpoint = "127.0.0.1:9090" };
        await using var service = CreateService(registrar, 0, directory.Path, new RecordingGuardLauncher(events), loggerProvider: logs);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["restore-snapshot:exact-prior-settings", "capture", "set", "launch", "restore", "restored"], events);
        Assert.Contains(logs.Entries, entry => entry.EventId == 1312);
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId is 1313 or 1314);
    }

    [Fact]
    public async Task A_crashed_owner_that_recorded_its_applied_settings_hands_them_to_the_restore()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        using (var marker = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System))
        {
            marker.Write(new IPEndPoint(IPAddress.Loopback, 9090), "exact-prior-settings");
            marker.RecordApplied("exact-applied-settings");
        }

        var events = new List<string>();
        var registrar = new RecordingSystemProxyRegistrar(events) { OwnedEndpoint = "127.0.0.1:9090" };
        await using var service = CreateService(registrar, 0, directory.Path, new RecordingGuardLauncher(events));

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["restore-snapshot:exact-prior-settings", "restore-applied:exact-applied-settings"], events.Take(2));
    }

    [Fact]
    public async Task The_settings_applied_by_set_are_recorded_for_crash_recovery()
    {
        using var directory = new TemporaryDirectory();
        await using var service = CreateService(new RecordingSystemProxyRegistrar { FailRestore = true }, 0, directory.Path);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        using var marker = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System);
        Assert.Equal("prior-system-proxy-settings", marker.PreviousSettingsSnapshot);
        Assert.Equal("applied-system-proxy-settings", marker.AppliedSettingsSnapshot);
    }

    [Fact]
    public async Task A_start_without_a_marker_does_not_clear_and_holds_the_marker()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), 0, directory.Path, new RecordingGuardLauncher(events));

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
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), 0, directory.Path, new RecordingGuardLauncher(events), TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(TestContext.Current.CancellationToken));

        Assert.Empty(events);
    }

    [Fact]
    public async Task A_clean_stop_restores_then_tells_the_guard_and_removes_the_marker()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), 0, directory.Path, new RecordingGuardLauncher(events));
        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(MarkerPath(directory)));

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["capture", "set", "launch", "restore", "restored"], events);
        Assert.False(File.Exists(MarkerPath(directory)));
    }

    [Fact]
    public async Task A_guard_that_cannot_start_restores_the_proxy_releases_the_marker_and_fails_startup()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        await using var service = CreateService(
            new RecordingSystemProxyRegistrar(events),
            0,
            directory.Path,
            new RecordingGuardLauncher(events) { Fail = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(TestContext.Current.CancellationToken));
        var port = service.Address.Port;
        Assert.NotEqual(0, port);

        Assert.Equal(["capture", "set", "launch", "restore"], events);
        Assert.False(File.Exists(MarkerPath(directory)));
        using var reopened = new FileStream(MarkerPath(directory), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.False(service.Server.ProxyRunning, "The listener must be released.");
    }

    [Fact]
    public async Task Without_system_proxy_configuration_there_is_no_marker_guard_or_registrar_call()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        await using var service = new TitaniumProxyService(
            new RecordingSystemProxyRegistrar(events),
            new ProxyRuntimeOptions(IPAddress.Loopback, 0, false, false, directory.Path),
            new ProviderRequestInspector(new PassThroughCompressionPipeline(), new CanonicalProviderHosts(), new NoRequestCapture(), new CcrOff(), NullLogger<ProviderRequestInspector>.Instance, TimeProvider.System, new NoGainLedger()),
            new CanonicalProviderHosts(),
            new RecordingGuardLauncher(events),
            NullLoggerFactory.Instance,
            TimeSpan.Zero,
            TimeProvider.System);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(events);
        Assert.False(File.Exists(MarkerPath(directory)));
    }

    [Fact]
    public async Task A_guard_that_died_early_does_not_stop_a_clean_stop()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        var launcher = new RecordingGuardLauncher(events) { ReportThrows = true };
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), 0, directory.Path, launcher);
        await service.StartAsync(TestContext.Current.CancellationToken);
        var port = service.Address.Port;
        Assert.NotEqual(0, port);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["capture", "set", "launch", "restore", "restored"], events);
        Assert.Equal(1, launcher.DisposeCount);
        Assert.False(File.Exists(MarkerPath(directory)));
        Assert.False(service.Server.ProxyRunning, "The listener must be released.");
    }

    [Fact]
    public async Task A_restore_that_fails_keeps_the_marker_and_does_not_tell_the_guard()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        var launcher = new RecordingGuardLauncher(events);
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events) { FailRestore = true }, 0, directory.Path, launcher);
        await service.StartAsync(TestContext.Current.CancellationToken);
        var port = service.Address.Port;
        Assert.NotEqual(0, port);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["capture", "set", "launch", "restore"], events);
        Assert.Equal(1, launcher.DisposeCount);
        Assert.True(new FileInfo(MarkerPath(directory)).Length > 0);
        Assert.False(service.Server.ProxyRunning, "The listener must be released.");
    }

    [Fact]
    public async Task The_marker_is_emptied_before_the_guard_is_told_the_proxy_was_restored()
    {
        using var directory = new TemporaryDirectory();
        var events = new List<string>();
        var launcher = new RecordingGuardLauncher(events);
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), 0, directory.Path, launcher);
        await service.StartAsync(TestContext.Current.CancellationToken);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, launcher.MarkerLengthWhenRestoredWasReported);
    }

    [Fact]
    public async Task A_marker_held_briefly_by_a_dying_owner_does_not_fail_startup()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var holder = new FileStream(MarkerPath(directory), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var clock = new TimerObservingTimeProvider();
        var events = new List<string>();
        await using var service = CreateService(new RecordingSystemProxyRegistrar(events), 0, directory.Path, new RecordingGuardLauncher(events), TimeSpan.FromSeconds(10), clock);

        var starting = service.StartAsync(TestContext.Current.CancellationToken);
        await Task.WhenAny(clock.FirstTimerCreated, starting).WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(starting.IsCompleted, "startup gave up instead of waiting for the marker");
        await holder.DisposeAsync();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await starting.WaitAsync(TestContext.Current.CancellationToken);

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
        TimeSpan? markerWait = null,
        TimeProvider? timeProvider = null,
        RecordingLoggerProvider? loggerProvider = null)
    {
        return new TitaniumProxyService(
            registrar,
            new ProxyRuntimeOptions(IPAddress.Loopback, port, true, false, dataDirectory),
            new ProviderRequestInspector(new PassThroughCompressionPipeline(), new CanonicalProviderHosts(), new NoRequestCapture(), new CcrOff(), NullLogger<ProviderRequestInspector>.Instance, TimeProvider.System, new NoGainLedger()),
            new CanonicalProviderHosts(),
            launcher ?? new RecordingGuardLauncher([]),
            loggerProvider is null ? NullLoggerFactory.Instance : new LoggerFactory([loggerProvider]),
            markerWait ?? TimeSpan.FromSeconds(1),
            timeProvider ?? TimeProvider.System);
    }

    private sealed class RecordingSystemProxyRegistrar(List<string>? events = null) : ISystemProxyRegistrar
    {
        public bool FailSet { get; init; }

        public bool FailRestore { get; init; }

        public ExplicitProxyEndPoint? EndPoint { get; private set; }

        public string? OwnedEndpoint { get; set; }

        public int RestoreCount { get; private set; }

        public bool RestoreFindsOwnership { get; init; } = true;

        /// <summary>The endpoints the system settings still point at; a capture replaces those it is asked to check.</summary>
        public IPEndPoint[] StaleValuesAt { get; init; } = [];

        /// <summary>Why cleaning the capture failed, or null when it ran.</summary>
        public Exception? CleanFailure { get; init; }

        /// <summary>Why loading the cleaned capture failed, or null when it loaded.</summary>
        public Exception? LoadFailure { get; init; }

        /// <summary>The arguments of the last <see cref="CaptureRestoreTarget" /> call, or null before one.</summary>
        public TargetCall? TargetRequest { get; private set; }

        public string Capture()
        {
            events?.Add("raw-capture");
            return "raw-system-proxy-settings";
        }

        public RestoreTarget CaptureRestoreTarget(IReadOnlyCollection<IPEndPoint> staleEndpoints, string? previousSnapshot)
        {
            events?.Add("capture");
            TargetRequest = new TargetCall(staleEndpoints.ToArray(), previousSnapshot);
            if (CleanFailure is not null || LoadFailure is not null)
            {
                return new RestoreTarget("prior-system-proxy-settings", []) { CleanFailure = CleanFailure, LoadFailure = LoadFailure };
            }

            var replaced = staleEndpoints.Where(StaleValuesAt.Contains).ToArray();
            return new RestoreTarget(replaced.Length > 0 ? "cleaned-settings" : "prior-system-proxy-settings", replaced);
        }

        public bool IsOwned(string endpoint) => endpoint == OwnedEndpoint;

        public bool IsOwned(string endpoint, string snapshot, string? appliedSnapshot) => endpoint == OwnedEndpoint;

        public Task<bool> RestoreIfOwned(string endpoint, string snapshot, string? appliedSnapshot)
        {
            if (endpoint != OwnedEndpoint)
            {
                return Task.FromResult(false);
            }

            events?.Add($"restore-snapshot:{snapshot}");
            if (appliedSnapshot is not null)
            {
                events?.Add($"restore-applied:{appliedSnapshot}");
            }

            OwnedEndpoint = null;
            RestoreCount++;
            return Task.FromResult(true);
        }

        public string Set(ExplicitProxyEndPoint endPoint)
        {
            if (FailSet)
            {
                throw new InvalidOperationException("networksetup failed");
            }

            events?.Add("set");
            EndPoint = endPoint;
            OwnedEndpoint = $"{endPoint.IpAddress}:{endPoint.Port}";
            return "applied-system-proxy-settings";
        }

        public bool Restore()
        {
            events?.Add("restore");
            RestoreCount++;
            if (FailRestore)
            {
                throw new InvalidOperationException("networksetup failed");
            }

            return RestoreFindsOwnership;
        }

        /// <inheritdoc />
        public Task ClearAsync()
        {
            events?.Add("clear");
            return Task.CompletedTask;
        }
    }

    /// <summary>The stale endpoints and previous snapshot a restore-target capture was asked to clean against.</summary>
    private sealed record TargetCall(IPEndPoint[] StaleEndpoints, string? PreviousSnapshot);

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
