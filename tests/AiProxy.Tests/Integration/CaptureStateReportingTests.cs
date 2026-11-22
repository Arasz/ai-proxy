using System.Text.Json;
using AiProxy.Cli;
using AiProxy.Client;
using AiProxy.Hosting.Proxy;
using AiProxy.Core.Capture;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console.Testing;

namespace AiProxy.Tests.Integration;

/// <summary>What <c>status</c> and <c>doctor</c> say about capture, read from the control pipe.</summary>
public sealed class CaptureStateReportingTests
{
    private const long Cap = 1_073_741_824;
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static CaptureState AtItsCapWithARecentError()
    {
        return new CaptureState(Cap, Cap, Now.AddDays(-3), Now.AddMinutes(-5), "disk I/O error", Now.AddMinutes(-10));
    }

    private static object OnWith(CaptureState state)
    {
        return new { enabled = true, state };
    }

    private sealed record Outcome(int Exit, string Output, string Error);

    private static async Task<Outcome> RunAsync(string[] args, Dictionary<string, string>? environment = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var console = new TestConsole();
        using var http = new HttpClient();
        var variables = environment ?? [];
        var runtime = new GainRuntime(console, output, error, new ProxyClient(http), "/work", false, new FakeTimeProvider(Now), null, name => variables.GetValueOrDefault(name));
        var exit = await new CliApplication(runtime).RunAsync(args, TestContext.Current.CancellationToken);
        return new Outcome(exit, output + console.Output, error.ToString());
    }

    [Fact]
    public async Task Status_prints_capture_at_its_cap_the_last_write_error_and_the_log_path()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        await using var fake = new FakeControlOwner(directory.Path, dummy.Info() with { Capture = OnWith(AtItsCapWithARecentError()) });

        var outcome = await RunAsync(["status", "--data-directory", directory.Path]);

        Assert.Equal(0, outcome.Exit);
        Assert.Contains("capture: on", outcome.Output);
        Assert.Contains("1024.0 MiB of 1024.0 MiB", outcome.Output);
        Assert.Contains("2026-11-18", outcome.Output);
        Assert.Contains("2026-11-21 11:55:00Z", outcome.Output);
        Assert.Contains("disk I/O error", outcome.Output);
        Assert.Contains("2026-11-21 11:50:00Z", outcome.Output);
        Assert.Contains(Path.Combine(directory.Path, "proxy.log"), outcome.Output);
    }

    [Fact]
    public async Task Status_json_carries_every_capture_field()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        await using var fake = new FakeControlOwner(directory.Path, dummy.Info() with { Capture = OnWith(AtItsCapWithARecentError()) });

        var outcome = await RunAsync(["status", "--data-directory", directory.Path, "-f", "json"]);

        using var json = JsonDocument.Parse(outcome.Output);
        var capture = json.RootElement.GetProperty("capture");
        Assert.True(capture.GetProperty("enabled").GetBoolean());
        var state = capture.GetProperty("state");
        Assert.Equal(Cap, state.GetProperty("liveBytes").GetInt64());
        Assert.Equal(Cap, state.GetProperty("capBytes").GetInt64());
        Assert.Equal("disk I/O error", state.GetProperty("lastWriteError").GetString());
        Assert.Equal(Path.Combine(directory.Path, "proxy.log"), json.RootElement.GetProperty("logFile").GetString());
    }

    [Fact]
    public async Task Status_shows_capture_off_when_the_owner_does_not_capture()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        await using var fake = new FakeControlOwner(directory.Path, dummy.Info() with { Capture = new { enabled = false } });

        var outcome = await RunAsync(["status", "--data-directory", directory.Path]);

        Assert.Equal(0, outcome.Exit);
        Assert.Contains("capture: off", outcome.Output);
    }

    [Fact]
    public async Task Status_against_an_older_owner_without_the_capture_field_shows_capture_unknown()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        await using var fake = new FakeControlOwner(directory.Path, dummy.Info());

        var outcome = await RunAsync(["status", "--data-directory", directory.Path]);

        Assert.Equal(0, outcome.Exit);
        Assert.Contains("capture: unknown", outcome.Output);
    }

    [Fact]
    public async Task Doctor_warns_when_the_last_capture_write_failed_within_the_hour()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        await using var fake = new FakeControlOwner(directory.Path, dummy.Info() with { Capture = OnWith(AtItsCapWithARecentError()) });

        var check = await RunDoctorCaptureCheckAsync(directory.Path, []);

        Assert.Equal("warn", check.GetProperty("status").GetString());
        Assert.Contains("disk I/O error", check.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Doctor_passes_when_the_last_capture_write_error_is_older_than_an_hour()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        var old = AtItsCapWithARecentError() with { LastWriteErrorAt = Now.AddHours(-2) };
        await using var fake = new FakeControlOwner(directory.Path, dummy.Info() with { Capture = OnWith(old) });

        var check = await RunDoctorCaptureCheckAsync(directory.Path, []);

        Assert.Equal("pass", check.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Doctor_warns_when_capture_is_off_while_configured_on()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        await using var fake = new FakeControlOwner(directory.Path, dummy.Info() with { Capture = new { enabled = false } });

        var check = await RunDoctorCaptureCheckAsync(directory.Path, new Dictionary<string, string> { ["Proxy__Capture"] = "true" });

        Assert.Equal("warn", check.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Doctor_leaves_the_exit_code_alone_for_an_older_owner_without_the_capture_field()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        await using var fake = new FakeControlOwner(directory.Path, dummy.Info());

        var result = await new AiProxy.Doctor.CaptureCheck(directory.Path, new FakeTimeProvider(Now), false).RunAsync(TestContext.Current.CancellationToken);
        var healthy = new AiProxy.Core.Doctor.CheckResult("other", AiProxy.Core.Doctor.CheckStatus.Pass, "fine");

        Assert.Equal(0, new AiProxy.Core.Doctor.DoctorReport([healthy, result]).ExitCode);
    }

    [Fact]
    public async Task Doctor_exits_2_when_the_capture_write_failed_and_nothing_else_is_wrong()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        await using var fake = new FakeControlOwner(directory.Path, dummy.Info() with { Capture = OnWith(AtItsCapWithARecentError()) });

        var result = await new AiProxy.Doctor.CaptureCheck(directory.Path, new FakeTimeProvider(Now), false).RunAsync(TestContext.Current.CancellationToken);
        var healthy = new AiProxy.Core.Doctor.CheckResult("other", AiProxy.Core.Doctor.CheckStatus.Pass, "fine");

        Assert.Equal(2, new AiProxy.Core.Doctor.DoctorReport([healthy, result]).ExitCode);
    }

    [Fact]
    public async Task The_real_control_reply_round_trips_every_capture_field()
    {
        using var directory = new TemporaryDirectory();
        using var dummy = new DummyOwner();
        var sent = AtItsCapWithARecentError();
        var fakeInfo = dummy.Info();
        var info = new ProxyControl.ProxyInfo(fakeInfo.Product, fakeInfo.Protocol, fakeInfo.Nonce, fakeInfo.Pid, fakeInfo.StartUtc, fakeInfo.ProxyUrl, fakeInfo.ApiUrls, fakeInfo.ProcessStartToken!);
        await using var control = new ProxyControl(directory.Path, info, new StubLifetime(), _ => Task.FromResult(new ProxyControl.CaptureInfo(true, sent)));
        await control.Start(StartupLease.NoOpLease);

        var reading = await ProxyControl.ReadOwnerAsync(directory.Path, TestContext.Current.CancellationToken);

        Assert.Equal(ProxyControl.OwnerState.Running, reading.State);
        Assert.Equal(new ProxyControl.CaptureInfo(true, sent), reading.Owner!.Capture);
    }

    private static async Task<JsonElement> RunDoctorCaptureCheckAsync(string directory, Dictionary<string, string> environment)
    {
        var outcome = await RunAsync(["doctor", "--data-directory", directory, "--timeout", "2", "--format", "json", "--endpoint", "http://127.0.0.1:1", "--proxy", "http://127.0.0.1:2"], environment);
        using var json = JsonDocument.Parse(outcome.Output);
        return json.RootElement.GetProperty("checks").EnumerateArray().Single(static check => check.GetProperty("name").GetString() == "capture").Clone();
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
