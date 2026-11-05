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
    public async Task Stop_with_no_owner_reports_not_running_and_leaves_the_configured_port_alone()
    {
        using var directory = new TemporaryDirectory();
        using var sentinel = new TcpListener(IPAddress.Loopback, 0);
        sentinel.Start();
        var port = ((IPEndPoint)sentinel.LocalEndpoint).Port;
        var accepted = Task.Run(async () =>
        {
            using var client = await sentinel.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await using var stop = Child.StartWith(directory.Path, ["stop"], false,
            new Dictionary<string, string?> { ["Proxy__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        await ExitAsync(stop);

        Assert.Equal(0, stop.Process.ExitCode);
        Assert.Contains("not running", stop.Output);
        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await accepted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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
    public async Task A_restart_started_while_stop_holds_the_claim_fails_fast()
    {
        using var directory = new TemporaryDirectory();
        await using var owner = Child.Start(directory.Path, false);
        await owner.ReadyAsync();
        await ControlPipeClient.ReadInfoAsync(directory.Path);
        Child? contender = null;
        try
        {
            var stopped = await new ProxyStop(new ProxyLease()).StopAsync(directory.Path, TestContext.Current.CancellationToken, _ =>
            {
                contender = Child.Start(directory.Path, true);
                Assert.True(contender.Process.WaitForExit(Wait), contender.Output);
            });

            Assert.Equal(owner.Process.Id, stopped?.Pid);
            Assert.NotEqual(0, contender!.Process.ExitCode);
            Assert.Contains("in progress", contender.Output);
        }
        finally
        {
            if (contender is not null)
            {
                await contender.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Stop_exits_1_when_the_owner_refuses_shutdown()
    {
        using var directory = new TemporaryDirectory();
        await using var runtime = new ProxyLease().AcquireRuntimeLease(directory.Path);
        await using var fake = new FakeControlOwner(directory.Path, CurrentProcessInfo(true), false);

        await using var stop = Child.StartWith(directory.Path, ["stop"]);
        await ExitAsync(stop);

        Assert.Equal(1, stop.Process.ExitCode);
        Assert.Contains("refused", stop.Output);
        Assert.Equal(["info", "stop"], fake.Commands);
    }

    [Fact]
    public async Task Stop_exits_1_and_never_kills_when_the_owner_identity_changed()
    {
        using var directory = new TemporaryDirectory();
        await using var runtime = new ProxyLease().AcquireRuntimeLease(directory.Path);
        await using var fake = new FakeControlOwner(directory.Path, CurrentProcessInfo(false), true);

        await using var stop = Child.StartWith(directory.Path, ["stop"]);
        await ExitAsync(stop);

        Assert.Equal(1, stop.Process.ExitCode);
        Assert.Contains("identity", stop.Output);
        Assert.Equal(["info"], fake.Commands);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.False(self.HasExited);
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
        var other = CurrentProcessInfo(true) with { Product = "other" };
        await using var fake = new FakeControlOwner(directory.Path, other);

        await using var status = Child.StartWith(directory.Path, ["status"]);
        await ExitAsync(status);

        Assert.Equal(1, status.Process.ExitCode);
        Assert.Contains("compatible", status.Output);
    }

    private static ControlPipeClient.Info CurrentProcessInfo(bool validIdentity)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var token = validIdentity ? new ProcessIdentity(self).StartToken : "native:0";
        return new ControlPipeClient.Info("ai-proxy", 1, "fake-nonce", self.Id, self.StartTime.ToUniversalTime(),
            "http://127.0.0.1:1", ["http://127.0.0.1:2"], token);
    }

    private static async Task ExitAsync(ProxyChild child)
    {
        using var deadline = new CancellationTokenSource(Wait);
        await child.Process.WaitForExitAsync(deadline.Token);
    }
}
