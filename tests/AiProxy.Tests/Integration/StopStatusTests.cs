using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AiProxy.Hosting.Proxy;
using Child = AiProxy.Tests.Integration.ProxyChild;

namespace AiProxy.Tests.Integration;

/// <summary>Real child processes running <c>ai-proxy stop</c> and <c>ai-proxy status</c> against owners and scripted control pipes.</summary>
public sealed class StopStatusTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(40);

    [Fact]
    public async Task Stop_shuts_the_running_owner_down_gracefully()
    {
        using var directory = new TemporaryDirectory();
        await using var owner = Child.Start(directory.Path, false);
        await owner.ReadyAsync();
        await ControlPipeClient.ReadInfoAsync(directory.Path);

        await using var stop = Child.StartWith(directory.Path, ["stop"]);
        await ExitAsync(stop);

        Assert.Equal(0, stop.Process.ExitCode);
        await ExitAsync(owner);
        Assert.Contains("Application is shutting down", owner.Output);
        Assert.Equal(0, owner.Process.ExitCode);
    }

    [Fact]
    public async Task Stop_with_no_owner_reports_not_running_and_never_touches_the_configured_port()
    {
        using var directory = new TemporaryDirectory();
        using var sentinel = new TcpListener(IPAddress.Loopback, 0);
        sentinel.Start();
        var port = ((IPEndPoint)sentinel.LocalEndpoint).Port;

        await using var stop = Child.StartWith(directory.Path, ["stop"], false,
            new Dictionary<string, string?> { ["Proxy__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        await ExitAsync(stop);

        Assert.Equal(0, stop.Process.ExitCode);
        Assert.Contains("not running", stop.Output);
        Assert.False(sentinel.Pending());
        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Stop_finds_the_owner_through_an_upper_case_data_directory_variable()
    {
        using var directory = new TemporaryDirectory();
        using var elsewhere = new TemporaryDirectory();
        await using var owner = Child.Start(directory.Path, false);
        await owner.ReadyAsync();
        await ControlPipeClient.ReadInfoAsync(directory.Path);

        await using var stop = Child.StartWith(elsewhere.Path, ["stop"], true,
            new Dictionary<string, string?> { ["Proxy__DataDirectory"] = null, ["PROXY__DATADIRECTORY"] = directory.Path });
        await ExitAsync(stop);

        Assert.Equal(0, stop.Process.ExitCode);
        Assert.Contains("Stopped", stop.Output);
        await ExitAsync(owner);
    }

    [Fact]
    public async Task Stop_while_a_start_claim_is_held_exits_1_and_leaves_the_owner_running()
    {
        using var directory = new TemporaryDirectory();
        await using var owner = Child.Start(directory.Path, false);
        await owner.ReadyAsync();
        var before = await ControlPipeClient.ReadInfoAsync(directory.Path);

        await using (new ProxyLease().AcquireStartupLease(directory.Path))
        {
            await using var stop = Child.StartWith(directory.Path, ["stop"]);
            await ExitAsync(stop);
            Assert.Equal(1, stop.Process.ExitCode);
            Assert.Contains("stop is in progress", stop.Output);
        }

        Assert.False(owner.Process.HasExited);
        Assert.Equal(before.Nonce, (await ControlPipeClient.ReadInfoAsync(directory.Path)).Nonce);
    }

    [Fact]
    public async Task A_restart_started_while_stop_waits_for_the_owner_to_exit_fails_fast()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        await using var runtime = new ProxyLease().AcquireRuntimeLease(directory.Path);
        await using var fake = new FakeControlOwner(directory.Path, dummy.Info(), true);

        await using var stop = Child.StartWith(directory.Path, ["stop"]);
        await WaitForAsync(() => fake.Commands.Contains("stop"));
        Assert.False(stop.Process.HasExited);

        await using var contender = Child.Start(directory.Path, true);
        await ExitAsync(contender);
        Assert.NotEqual(0, contender.Process.ExitCode);
        Assert.Contains("in progress", contender.Output);
        Assert.False(stop.Process.HasExited);

        await dummy.EndAsync();
        await ExitAsync(stop);
        Assert.Equal(0, stop.Process.ExitCode);
    }

    [Fact]
    public async Task Stop_exits_1_and_leaves_the_owner_alive_when_it_refuses_shutdown()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        await using var runtime = new ProxyLease().AcquireRuntimeLease(directory.Path);
        await using var fake = new FakeControlOwner(directory.Path, dummy.Info(), false);

        await using var stop = Child.StartWith(directory.Path, ["stop"]);
        await ExitAsync(stop);

        Assert.Equal(1, stop.Process.ExitCode);
        Assert.Contains("refused", stop.Output);
        Assert.Equal(["info", "stop"], fake.Commands);
        Assert.True(dummy.Alive);
    }

    [Fact]
    public async Task Stop_exits_1_and_leaves_the_process_alive_when_the_owner_identity_changed()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        await using var runtime = new ProxyLease().AcquireRuntimeLease(directory.Path);
        await using var fake = new FakeControlOwner(directory.Path, dummy.Info(false), true);

        await using var stop = Child.StartWith(directory.Path, ["stop"]);
        await ExitAsync(stop);

        Assert.Equal(1, stop.Process.ExitCode);
        Assert.Contains("identity", stop.Output);
        Assert.Equal(["info"], fake.Commands);
        Assert.True(dummy.Alive);
    }

    [Fact]
    public async Task Status_prints_the_owner_without_secrets_and_leaves_it_running()
    {
        using var directory = new TemporaryDirectory();
        await using var owner = Child.Start(directory.Path, false);
        await owner.ReadyAsync();
        var info = await ControlPipeClient.ReadInfoAsync(directory.Path);

        await using var status = Child.StartWith(directory.Path, ["status", "-f", "json"]);
        await ExitAsync(status);

        Assert.Equal(0, status.Process.ExitCode);
        using var json = JsonDocument.Parse(status.Output);
        var root = json.RootElement;
        Assert.Equal(info.Pid, root.GetProperty("pid").GetInt32());
        Assert.Equal(info.ProxyUrl, root.GetProperty("proxyUrl").GetString());
        Assert.Equal(info.ApiUrls, root.GetProperty("apiUrls").EnumerateArray().Select(static url => url.GetString()).ToArray());
        Assert.DoesNotContain(info.Nonce, status.Output);
        Assert.DoesNotContain("nonce", status.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("startToken", status.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(owner.Process.HasExited);

        await ControlPipeClient.StopAsync(directory.Path);
        await ExitAsync(owner);
        Assert.Equal(0, owner.Process.ExitCode);
    }

    [Fact]
    public async Task Status_with_nothing_running_exits_3()
    {
        using var directory = new TemporaryDirectory();

        await using var status = Child.StartWith(directory.Path, ["status"]);
        await ExitAsync(status);

        Assert.Equal(3, status.Process.ExitCode);
        Assert.Contains("no ai-proxy answered", status.Output);
    }

    [Fact]
    public async Task A_child_without_a_data_directory_setting_never_resolves_the_real_home()
    {
        using var directory = new TemporaryDirectory();

        await using var status = Child.StartWith(directory.Path, ["status"], true,
            new Dictionary<string, string?> { ["Proxy__DataDirectory"] = null, ["PROXY__DATADIRECTORY"] = null });
        await ExitAsync(status);

        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ai-proxy");
        Assert.Equal(3, status.Process.ExitCode);
        Assert.DoesNotContain(real, status.Output);
        Assert.Contains("ai-proxy-child-home-", status.Output);
    }

    [Fact]
    public async Task Status_exits_1_when_the_owner_does_not_answer()
    {
        using var directory = new TemporaryDirectory();
        await using var fake = new FakeControlOwner(directory.Path, null);

        await using var status = Child.StartWith(directory.Path, ["status"]);
        await ExitAsync(status);

        Assert.Equal(1, status.Process.ExitCode);
        Assert.Contains("did not answer", status.Output);
    }

    [Fact]
    public async Task Status_exits_1_when_the_owner_is_not_a_compatible_ai_proxy()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        var other = dummy.Info() with { Product = "other" };
        await using var fake = new FakeControlOwner(directory.Path, other);

        await using var status = Child.StartWith(directory.Path, ["status"]);
        await ExitAsync(status);

        Assert.Equal(1, status.Process.ExitCode);
        Assert.Contains("compatible", status.Output);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(Wait);
        while (!condition())
        {
            await Task.Delay(50, deadline.Token);
        }
    }

    private static async Task ExitAsync(ProxyChild child)
    {
        using var deadline = new CancellationTokenSource(Wait);
        await child.Process.WaitForExitAsync(deadline.Token);
    }
}
