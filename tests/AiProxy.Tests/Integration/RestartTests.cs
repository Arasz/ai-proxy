using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiProxy.Hosting;

namespace AiProxy.Tests.Integration;

/// <summary>Real child processes own temporary directories and ephemeral loopback listeners.</summary>
public sealed class RestartTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Restart_replaces_a_legacy_proxy_without_a_control_pipe_and_preserves_its_ports()
    {
        using var directory = new TemporaryDirectory();
        await using var legacy = Child.StartLegacy(directory.Path);
        await legacy.ReadyAsync();
        var line = legacy.Output.Split('\n').Single(value => value.StartsWith("Application started LEGACY ", StringComparison.Ordinal));
        using var endpoints = JsonDocument.Parse(line["Application started LEGACY ".Length..]);
        await using var replacement = Child.Start(directory.Path, true, false);
        await replacement.ReadyAsync();
        Assert.True(legacy.Process.HasExited);
        var after = await ReadInfoAsync(directory.Path);
        Assert.Equal(replacement.Process.Id, after.Pid);
        Assert.Equal(endpoints.RootElement.GetProperty("proxy").GetString(), after.ProxyUrl);
        Assert.Equal(endpoints.RootElement.GetProperty("api").GetString(), after.ApiUrls.Single());
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        Assert.Contains("ok", await http.GetStringAsync($"{after.ApiUrls.Single()}/health", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_legacy_recovery_guard_finishes_cleanup_without_being_signalled()
    {
        using var directory = new TemporaryDirectory();
        await using var guard = Child.StartLegacy(directory.Path, guard: true);
        await guard.ReadyAsync();
        await using var replacement = Child.Start(directory.Path, true);
        await replacement.ReadyAsync();
        await guard.ExitAsync();
        Assert.Equal(0, guard.Process.ExitCode);
        Assert.Equal(replacement.Process.Id, (await ReadInfoAsync(directory.Path)).Pid);
    }

    [Fact]
    public async Task A_verified_legacy_proxy_that_ignores_graceful_shutdown_is_terminated_and_replaced()
    {
        using var directory = new TemporaryDirectory();
        await using var legacy = Child.StartLegacy(directory.Path, ignoreTermination: true);
        await legacy.ReadyAsync();
        await using var replacement = Child.Start(directory.Path, true, false);
        await replacement.ReadyAsync();
        Assert.True(legacy.Process.HasExited);
        Assert.NotEqual(0, legacy.Process.ExitCode);
        Assert.Equal(replacement.Process.Id, (await ReadInfoAsync(directory.Path)).Pid);
    }

    [Fact]
    public async Task A_legacy_proxy_with_an_unresponsive_api_can_restart_and_preserves_its_proxy_port()
    {
        using var directory = new TemporaryDirectory();
        await using var legacy = Child.StartLegacy(directory.Path, true);
        await legacy.ReadyAsync();
        var line = legacy.Output.Split('\n').Single(value => value.StartsWith("Application started LEGACY ", StringComparison.Ordinal));
        using var endpoints = JsonDocument.Parse(line["Application started LEGACY ".Length..]);
        await using var replacement = Child.Start(directory.Path, true, false, "--urls=http://127.0.0.1:0");
        await replacement.ReadyAsync();
        Assert.True(legacy.Process.HasExited);
        var after = await ReadInfoAsync(directory.Path);
        Assert.Equal(endpoints.RootElement.GetProperty("proxy").GetString(), after.ProxyUrl);
    }

    [Fact]
    public async Task An_unrelated_listener_on_another_address_is_not_exempted_by_the_old_port_number()
    {
        using var directory = new TemporaryDirectory();
        await using var owner = Child.Start(directory.Path, false);
        await owner.ReadyAsync();
        var before = await ReadInfoAsync(directory.Path);
        var port = new Uri(before.ProxyUrl).Port;
        using var sentinel = new TcpListener(IPAddress.IPv6Loopback, port);
        sentinel.Server.DualMode = false;
        sentinel.Start();
        await using var contender = Child.Start(directory.Path, true, true, $"--urls=http://[::1]:{port}");
        await contender.ExitAsync();
        Assert.NotEqual(0, contender.Process.ExitCode);
        Assert.False(owner.Process.HasExited);
        Assert.Equal(before.Nonce, (await ReadInfoAsync(directory.Path)).Nonce);
        using var probe = new TcpClient(AddressFamily.InterNetworkV6);
        await probe.ConnectAsync(IPAddress.IPv6Loopback, port, TestContext.Current.CancellationToken);
        using var accepted = await sentinel.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        Assert.True(accepted.Connected);
    }

    [Fact]
    public async Task A_proxy_override_colliding_with_the_inherited_api_is_rejected_before_shutdown()
    {
        using var directory = new TemporaryDirectory();
        await using var owner = Child.Start(directory.Path, false);
        await owner.ReadyAsync();
        var before = await ReadInfoAsync(directory.Path);
        await using var contender = Child.Start(directory.Path, true, false, $"--Proxy:Port={new Uri(before.ApiUrls.Single()).Port}");
        await contender.ExitAsync();
        Assert.NotEqual(0, contender.Process.ExitCode);
        Assert.False(owner.Process.HasExited);
        Assert.Equal(before.Nonce, (await ReadInfoAsync(directory.Path)).Nonce);
    }

    [Fact]
    public async Task Restart_without_an_owner_starts_an_owned_instance_with_actual_endpoints()
    {
        using var directory = new TemporaryDirectory();
        await using var child = Child.Start(directory.Path, true);
        await child.ReadyAsync();
        var info = await ReadInfoAsync(directory.Path);
        Assert.Equal("ai-proxy", info.Product);
        Assert.Equal(1, info.Protocol);
        Assert.Equal(child.Process.Id, info.Pid);
        Assert.InRange(new Uri(info.ProxyUrl).Port, 1, 65535);
        using var http = new HttpClient();
        Assert.Contains("ok", await http.GetStringAsync($"{info.ApiUrls.Single()}/health", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restart_waits_for_the_original_process_and_inherits_actual_endpoints()
    {
        using var directory = new TemporaryDirectory();
        await using var original = Child.Start(directory.Path, false);
        await original.ReadyAsync();
        var before = await ReadInfoAsync(directory.Path);
        await using var replacement = Child.Start(directory.Path, true, false);
        await replacement.ReadyAsync();
        Assert.True(original.Process.HasExited);
        Assert.Equal(0, original.Process.ExitCode);
        var after = await ReadInfoAsync(directory.Path);
        Assert.Equal(replacement.Process.Id, after.Pid);
        Assert.NotEqual(before.Nonce, after.Nonce);
        Assert.Equal(before.ProxyUrl, after.ProxyUrl);
        Assert.Equal(before.ApiUrls, after.ApiUrls);
        using var http = new HttpClient();
        Assert.Contains("ok", await http.GetStringAsync($"{after.ApiUrls.Single()}/health", TestContext.Current.CancellationToken));
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            using var socket = await upstream.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(TestContext.Current.CancellationToken)))
            {
            }

            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: close\r\n\r\nforwarded"u8.ToArray(), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        using var proxied = new HttpClient(new HttpClientHandler { Proxy = new WebProxy(after.ProxyUrl), UseProxy = true });
        Assert.Equal("forwarded", await proxied.GetStringAsync($"http://127.0.0.1:{upstreamPort}/", TestContext.Current.CancellationToken));
        await received.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Restart_help_bypasses_invalid_configuration_and_creates_no_state()
    {
        using var directory = new TemporaryDirectory();
        await using var child = Child.Start(directory.Path, true, true, "--help", "--Proxy:Port", "invalid");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await child.Process.WaitForExitAsync(deadline.Token);
        Assert.Equal(0, child.Process.ExitCode);
        Assert.Contains("--restart", child.Output);
        Assert.False(Directory.Exists(directory.Path));
    }

    [Fact]
    public async Task Held_legacy_marker_rejects_restart_before_touching_certificate_files()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var certificate = Path.Combine(directory.Path, "root-ca.pfx");
        var pem = Path.Combine(directory.Path, "root-ca.pem");
        await File.WriteAllTextAsync(certificate, "legacy private certificate bytes", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(pem, "legacy public certificate bytes", TestContext.Current.CancellationToken);
        var markerPath = Path.Combine(directory.Path, "system-proxy.lock");
        await File.WriteAllTextAsync(markerPath, "127.0.0.1:12345", TestContext.Current.CancellationToken);
        using (var marker = new FileStream(markerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await using var child = Child.Start(directory.Path, true);
            await child.ExitAsync();
            Assert.NotEqual(0, child.Process.ExitCode);
            Assert.Contains("Stop", child.Output);
            Assert.Contains("manually", child.Output);
        }

        Assert.Equal("legacy private certificate bytes", await File.ReadAllTextAsync(certificate, TestContext.Current.CancellationToken));
        Assert.Equal("legacy public certificate bytes", await File.ReadAllTextAsync(pem, TestContext.Current.CancellationToken));
        Assert.Equal("127.0.0.1:12345", await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Occupied_requested_endpoint_fails_before_certificates_and_leaves_the_listener_alive()
    {
        using var directory = new TemporaryDirectory();
        using var sentinel = new TcpListener(IPAddress.Loopback, 0);
        sentinel.Start();
        var port = ((IPEndPoint)sentinel.LocalEndpoint).Port;
        await using var child = Child.Start(directory.Path, true, true, $"--Proxy:Port={port}");
        await child.ExitAsync();
        Assert.NotEqual(0, child.Process.ExitCode);
        Assert.Contains("already in use", child.Output);
        Assert.Contains("manually", child.Output);
        Assert.False(File.Exists(Path.Combine(directory.Path, "root-ca.pfx")));
        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        using var accepted = await sentinel.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        Assert.True(accepted.Connected);
    }

    [Fact]
    public async Task Normal_start_cannot_replace_the_owner_or_unlink_its_control_socket()
    {
        using var directory = new TemporaryDirectory();
        await using var owner = Child.Start(directory.Path, false);
        await owner.ReadyAsync();
        var before = await ReadInfoAsync(directory.Path);
        await using var contender = Child.Start(directory.Path, false);
        await contender.ExitAsync();
        Assert.NotEqual(0, contender.Process.ExitCode);
        Assert.Equal(before.Nonce, (await ReadInfoAsync(directory.Path)).Nonce);
        Assert.False(owner.Process.HasExited);
    }

    [Fact]
    public async Task Crash_releases_the_lease_and_allows_a_stale_control_socket_to_be_reclaimed()
    {
        using var directory = new TemporaryDirectory();
        await using var original = Child.Start(directory.Path, false);
        await original.ReadyAsync();
        var before = await ReadInfoAsync(directory.Path);
        original.Process.Kill();
        await original.ExitAsync();
        await using var replacement = Child.Start(directory.Path, true);
        await replacement.ReadyAsync();
        Assert.NotEqual(before.Nonce, (await ReadInfoAsync(directory.Path)).Nonce);
        Assert.True(File.Exists(Path.Combine(directory.Path, "process.lock")));
    }

    [Fact]
    public async Task Wrong_nonce_does_not_stop_the_owner_and_the_next_client_can_connect()
    {
        using var directory = new TemporaryDirectory();
        await using var child = Child.Start(directory.Path, false);
        await child.ReadyAsync();
        await using var pipe = await ConnectAsync(directory.Path);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteAsync(pipe, new { command = "info", protocol = 1 }, deadline.Token);
        var info = await ReadAsync<Info>(pipe, deadline.Token);
        await WriteAsync(pipe, new { command = "stop", protocol = 1, nonce = "wrong-nonce" }, deadline.Token);
        var reply = await ReadAsync<JsonElement>(pipe, deadline.Token);
        Assert.False(reply.GetProperty("accepted").GetBoolean());
        Assert.Equal(info.Nonce, (await ReadInfoAsync(directory.Path)).Nonce);
        Assert.False(child.Process.HasExited);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("wrong-protocol")]
    [InlineData("oversized")]
    [InlineData("incomplete")]
    [InlineData("stalled")]
    public async Task Invalid_or_stalled_client_loses_only_its_connection(string kind)
    {
        using var directory = new TemporaryDirectory();
        await using var child = Child.Start(directory.Path, false);
        await child.ReadyAsync();
        var original = await ReadInfoAsync(directory.Path);
        await using var pipe = await ConnectAsync(directory.Path);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (kind == "wrong-protocol")
        {
            await WriteAsync(pipe, new { command = "info", protocol = 99 }, deadline.Token);
        }
        else if (kind != "stalled")
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, kind == "oversized" ? 16385 : kind == "malformed" ? 1 : 100);
            await pipe.WriteAsync(header, deadline.Token);
            await pipe.WriteAsync("!"u8.ToArray(), deadline.Token);
            await pipe.FlushAsync(deadline.Token);
        }

        // The hostile connection stays open while a valid client waits behind it.
        Assert.Equal(original.Nonce, (await ReadInfoAsync(directory.Path)).Nonce);
        Assert.False(child.Process.HasExited);
    }

    [Fact]
    public async Task Invalid_explicit_api_configuration_is_rejected_before_stopping_the_owner()
    {
        using var directory = new TemporaryDirectory();
        await using var owner = Child.Start(directory.Path, false);
        await owner.ReadyAsync();
        var before = await ReadInfoAsync(directory.Path);
        await using var contender = Child.Start(directory.Path, true, true, "--urls=invalid");
        await contender.ExitAsync();
        Assert.NotEqual(0, contender.Process.ExitCode);
        Assert.False(owner.Process.HasExited);
        Assert.Equal(before.Nonce, (await ReadInfoAsync(directory.Path)).Nonce);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_in_progress_start_excludes_other_starts_before_they_can_discover_an_owner(bool restart)
    {
        using var directory = new TemporaryDirectory();
        await using var owner = Child.Start(directory.Path, false);
        await owner.ReadyAsync();
        var before = await ReadInfoAsync(directory.Path);
        using var startup = new FileStream(Path.Combine(directory.Path, "startup.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        await using var contender = Child.Start(directory.Path, restart);
        await contender.ExitAsync();
        Assert.NotEqual(0, contender.Process.ExitCode);
        Assert.Contains("in progress", contender.Output);
        Assert.False(owner.Process.HasExited);
        Assert.Equal(before.Nonce, (await ReadInfoAsync(directory.Path)).Nonce);
    }

    [Theory]
    [InlineData("proxy")]
    [InlineData("api")]
    public async Task An_occupied_explicit_replacement_port_is_rejected_before_stopping_the_owner(string endpoint)
    {
        using var directory = new TemporaryDirectory();
        await using var owner = Child.Start(directory.Path, false);
        await owner.ReadyAsync();
        var before = await ReadInfoAsync(directory.Path);
        using var sentinel = new TcpListener(IPAddress.Loopback, 0);
        sentinel.Start();
        var port = ((IPEndPoint)sentinel.LocalEndpoint).Port;
        var option = endpoint == "proxy" ? $"--Proxy:Port={port}" : $"--urls=http://127.0.0.1:{port}";
        await using var contender = Child.Start(directory.Path, true, true, option);
        await contender.ExitAsync();
        Assert.NotEqual(0, contender.Process.ExitCode);
        Assert.False(owner.Process.HasExited);
        Assert.Equal(before.Nonce, (await ReadInfoAsync(directory.Path)).Nonce);
    }

    [Fact]
    public async Task A_shutdown_acknowledgement_is_not_completion_while_the_exact_owner_is_alive()
    {
        using var directory = new TemporaryDirectory();
        await using var child = Child.Start(directory.Path, false);
        await child.ReadyAsync();
        var actual = await ReadInfoAsync(directory.Path);
        using var fakeDirectory = new TemporaryDirectory();
        using var lease = ProxyInstance.Acquire(fakeDirectory.Path);
        await using var server = new NamedPipeServerStream(PipeName(fakeDirectory.Path), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(deadline.Token);
            await ReadAsync<JsonElement>(server, deadline.Token);
            await WriteAsync(server, actual, deadline.Token);
            var stop = await ReadAsync<JsonElement>(server, deadline.Token);
            Assert.Equal(actual.Nonce, stop.GetProperty("nonce").GetString());
            await WriteAsync(server, new { accepted = true }, deadline.Token);
            acknowledged.SetResult();
        }, TestContext.Current.CancellationToken);
        var operation = ProxyControl.StopOwnedAsync(fakeDirectory.Path, deadline.Token);
        await acknowledged.Task.WaitAsync(deadline.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => operation.WaitAsync(TimeSpan.FromMilliseconds(150), deadline.Token));
        Assert.False(child.Process.HasExited);
        await StopAsync(directory.Path);
        await child.ExitAsync();
        Assert.Equal(actual.Pid, (await operation).Pid);
        await serving;
    }

    [Theory]
    [InlineData("product")]
    [InlineData("protocol")]
    [InlineData("nonce")]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("start-token")]
    public async Task An_incompatible_or_mismatched_identity_is_rejected_without_a_stop_request(string field)
    {
        using var directory = new TemporaryDirectory();
        await using var child = Child.Start(directory.Path, false);
        await child.ReadyAsync();
        var actual = await ReadInfoAsync(directory.Path);
        var forged = field switch
        {
            "product" => actual with { Product = "unrelated" },
            "protocol" => actual with { Protocol = 99 },
            "nonce" => actual with { Nonce = "" },
            "pid" => actual with { Pid = Environment.ProcessId },
            "start" => actual with { StartUtc = DateTime.SpecifyKind(actual.StartUtc, DateTimeKind.Unspecified) },
            _ => actual with { ProcessStartToken = "changed-start-token" }
        };
        using var fakeDirectory = new TemporaryDirectory();
        using var lease = ProxyInstance.Acquire(fakeDirectory.Path);
        await using var server = new NamedPipeServerStream(PipeName(fakeDirectory.Path), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(deadline.Token);
            await ReadAsync<JsonElement>(server, deadline.Token);
            await WriteAsync(server, forged, deadline.Token);
            var next = new byte[1];
            Assert.Equal(0, await server.ReadAsync(next, deadline.Token));
        }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => ProxyControl.StopOwnedAsync(fakeDirectory.Path, deadline.Token));
        await serving;
        Assert.False(child.Process.HasExited);
        Assert.Equal(actual.Nonce, (await ReadInfoAsync(directory.Path)).Nonce);
    }

    [Fact]
    public async Task Explicit_ports_override_inherited_ports_without_a_reserve_and_release_race()
    {
        using var directory = new TemporaryDirectory();
        await using var child = Child.Start(directory.Path, false);
        await child.ReadyAsync();
        var before = await ReadInfoAsync(directory.Path);
        var oldProxy = new Uri(before.ProxyUrl).Port;
        var oldApi = new Uri(before.ApiUrls.Single()).Port;
        await using var replacement = Child.Start(directory.Path, true, false,
            $"--Proxy:Port={oldApi}", $"--urls=http://127.0.0.1:{oldProxy}");
        await replacement.ReadyAsync();
        var after = await ReadInfoAsync(directory.Path);
        Assert.Equal(oldApi, new Uri(after.ProxyUrl).Port);
        Assert.Equal(oldProxy, new Uri(after.ApiUrls.Single()).Port);
        Assert.True(child.Process.HasExited);
    }

    [Fact]
    public async Task A_concurrent_restarter_fails_while_the_winner_waits_for_exit_and_cannot_stop_its_replacement()
    {
        using var originalDirectory = new TemporaryDirectory();
        await using var original = Child.Start(originalDirectory.Path, false);
        await original.ReadyAsync();
        var actual = await ReadInfoAsync(originalDirectory.Path);
        using var directory = new TemporaryDirectory();
        using var oldLease = ProxyInstance.Acquire(directory.Path);
        await using var server = new NamedPipeServerStream(PipeName(directory.Path), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(deadline.Token);
            await ReadAsync<JsonElement>(server, deadline.Token);
            await WriteAsync(server, actual, deadline.Token);
            await ReadAsync<JsonElement>(server, deadline.Token);
            await WriteAsync(server, new { accepted = true }, deadline.Token);
            acknowledged.SetResult();
        }, TestContext.Current.CancellationToken);
        await using var winner = Child.Start(directory.Path, true, false);
        await acknowledged.Task.WaitAsync(deadline.Token);
        await using var loser = Child.Start(directory.Path, true);
        await loser.ExitAsync();
        Assert.NotEqual(0, loser.Process.ExitCode);
        Assert.Contains("in progress", loser.Output);
        Assert.False(original.Process.HasExited);
        await serving;
        await server.DisposeAsync();
        oldLease.Dispose();
        await StopAsync(originalDirectory.Path);
        await original.ExitAsync();
        await winner.ReadyAsync();
        var replacement = await ReadInfoAsync(directory.Path);
        Assert.Equal(winner.Process.Id, replacement.Pid);
        Assert.NotEqual(actual.Nonce, replacement.Nonce);
        Assert.False(winner.Process.HasExited);
        using var http = new HttpClient();
        Assert.Contains("ok", await http.GetStringAsync($"{replacement.ApiUrls.Single()}/health", deadline.Token));
    }

    [Fact]
    public async Task An_owner_without_a_control_peer_requires_manual_stop_without_creating_certificates()
    {
        using var directory = new TemporaryDirectory();
        using var owner = ProxyInstance.Acquire(directory.Path);
        await using var contender = Child.Start(directory.Path, true);
        await contender.ExitAsync();
        Assert.NotEqual(0, contender.Process.ExitCode);
        Assert.Contains("manually", contender.Output);
        Assert.Contains("control", contender.Output);
        Assert.False(File.Exists(Path.Combine(directory.Path, "root-ca.pfx")));
    }

    [Fact]
    public async Task Owner_reports_a_stable_process_start_token_independent_of_wall_clock_conversion()
    {
        using var directory = new TemporaryDirectory();
        await using var child = Child.Start(directory.Path, false);
        await child.ReadyAsync();
        var info = await ReadInfoAsync(directory.Path);
        Assert.False(string.IsNullOrWhiteSpace(info.ProcessStartToken));
        if (OperatingSystem.IsLinux())
        {
            var stat = await File.ReadAllTextAsync($"/proc/{child.Process.Id}/stat", TestContext.Current.CancellationToken);
            var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal($"linux:{fields[19]}", info.ProcessStartToken);
        }
        else
        {
            Assert.Equal($"native:{child.Process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}",
                info.ProcessStartToken);
        }
    }

    [Fact]
    public async Task A_client_disconnecting_after_info_does_not_prevent_subsequent_clients_from_connecting()
    {
        using var directory = new TemporaryDirectory();
        await using var child = Child.Start(directory.Path, false);
        await child.ReadyAsync();
        var original = await ReadInfoAsync(directory.Path);
        {
            await using var pipe = await ConnectAsync(directory.Path);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WriteAsync(pipe, new { command = "info", protocol = 1 }, deadline.Token);
            var info = await ReadAsync<Info>(pipe, deadline.Token);
            Assert.Equal(original.Nonce, info.Nonce);
        }

        var second = await ReadInfoAsync(directory.Path);
        Assert.Equal(original.Nonce, second.Nonce);
        Assert.False(child.Process.HasExited);
    }

    [Fact]
    public async Task Restart_with_empty_proxy_port_inherits_the_running_port()
    {
        using var directory = new TemporaryDirectory();
        await using var child = Child.Start(directory.Path, false);
        await child.ReadyAsync();
        var before = await ReadInfoAsync(directory.Path);
        await using var replacement = Child.Start(directory.Path, true, true, "--Proxy:Port=");
        await replacement.ReadyAsync();
        var after = await ReadInfoAsync(directory.Path);
        Assert.Equal(before.ProxyUrl, after.ProxyUrl);
        Assert.True(child.Process.HasExited);
    }

    private static async Task StopAsync(string directory)
    {
        await using var pipe = await ConnectAsync(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteAsync(pipe, new { command = "info", protocol = 1 }, deadline.Token);
        var info = await ReadAsync<Info>(pipe, deadline.Token);
        await WriteAsync(pipe, new { command = "stop", protocol = 1, nonce = info.Nonce }, deadline.Token);
        var reply = await ReadAsync<JsonElement>(pipe, deadline.Token);
        Assert.True(reply.GetProperty("accepted").GetBoolean());
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string directory)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(deadline.Token);
        return pipe;
    }

    private static string PipeName(string directory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return $"aip-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32]}";
    }

    private static async Task<Info> ReadInfoAsync(string directory)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(deadline.Token);
        await WriteAsync(pipe, new { command = "info", protocol = 1 }, deadline.Token);
        return await ReadAsync<Info>(pipe, deadline.Token);
    }

    private static async Task WriteAsync(Stream stream, object value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        Assert.InRange(length, 1, 16384);
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, Json)!;
    }

    private sealed record Info(string Product, int Protocol, string Nonce, int Pid, DateTime StartUtc, string ProxyUrl, string[] ApiUrls, string? ProcessStartToken = null);

    private sealed class Child(Process process) : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string> _output = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Process Process => process;
        public string Output => string.Join("\n", _output);

        public static Child StartLegacy(string directory, bool hungApi = false, bool ignoreTermination = false, bool guard = false)
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "legacy-fixture", OperatingSystem.IsWindows() ? "AiProxy.exe" : "AiProxy");
            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            if (guard)
            {
                start.ArgumentList.Add("--guard-system-proxy");
                start.ArgumentList.Add(directory);
            }

            start.Environment["LEGACY_PROXY_DIRECTORY"] = directory;
            start.Environment["LEGACY_HUNG_API"] = hungApi ? "true" : "false";
            start.Environment["LEGACY_IGNORE_TERMINATION"] = ignoreTermination ? "true" : "false";
            var process = new Process { StartInfo = start };
            var child = new Child(process);
            process.OutputDataReceived += (_, args) => child.Record(args.Data);
            process.ErrorDataReceived += (_, args) => child.Record(args.Data);
            Assert.True(process.Start());
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return child;
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(deadline.Token);
            process.Dispose();
        }

        public static Child Start(string directory, bool restart, bool ephemeral = true, params string[] extra)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AiProxy.dll"));
            if (restart)
            {
                start.ArgumentList.Add("--restart");
            }

            foreach (var argument in extra)
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["Proxy__DataDirectory"] = directory;
            start.Environment["Proxy__ConfigureSystemProxy"] = "false";
            start.Environment["Proxy__TrustRootCertificate"] = "false";
            start.Environment["Proxy__Compression__Enabled"] = "false";
            start.Environment["Proxy__Port"] = ephemeral ? "0" : null;
            start.Environment["ASPNETCORE_URLS"] = ephemeral ? "http://127.0.0.1:0" : null;
            var process = new Process { StartInfo = start };
            var child = new Child(process);
            process.OutputDataReceived += (_, args) => child.Record(args.Data);
            process.ErrorDataReceived += (_, args) => child.Record(args.Data);
            Assert.True(process.Start());
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return child;
        }

        private void Record(string? line)
        {
            if (line is null)
            {
                return;
            }

            _output.Enqueue(line);
            if (line.Contains("Application started", StringComparison.Ordinal))
            {
                _ready.TrySetResult();
            }
        }

        public async Task ExitAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(deadline.Token);
        }

        public async Task ReadyAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var exited = process.WaitForExitAsync(deadline.Token);
            var result = await Task.WhenAny(_ready.Task, exited).WaitAsync(deadline.Token);
            Assert.True(result == _ready.Task, Output);
        }
    }
}
