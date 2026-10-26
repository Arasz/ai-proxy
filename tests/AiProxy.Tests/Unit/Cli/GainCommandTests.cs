using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using AiProxy.Cli;
using AiProxy.Client;
using AiProxy.Core.Gain;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console.Testing;

namespace AiProxy.Tests.Unit.Cli;

public sealed class GainCommandTests
{
    [Fact]
    public async Task Json_output_is_clean_report_and_project_filter_is_callers_directory()
    {
        using var fixture = new Fixture();
        var exit = await new CliApplication(fixture.Runtime).RunAsync(["gain", "-p", "-f", "json"], TestContext.Current.CancellationToken);
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(fixture.Output.ToString());
        Assert.Equal(123, json.RootElement.GetProperty("summary").GetProperty("estimatedSavedTokens").GetInt64());
        Assert.Contains("project=%2Fwork%2Fproject", fixture.Handler.Uris.Single().Query);
        Assert.Equal("", fixture.Error.ToString());
    }

    [Theory]
    [InlineData("", "estimated initial-request")]
    [InlineData("--history", "request-one")]
    [InlineData("--daily", "Daily (UTC)")]
    [InlineData("--weekly", "Weekly (Monday UTC)")]
    [InlineData("--monthly", "Monthly (UTC)")]
    [InlineData("--all", "Monthly (UTC)")]
    [InlineData("--graph", "Daily savings")]
    [InlineData("--failures", "failed-stage")]
    [InlineData("--recalls", "preview-one")]
    [InlineData("--by-method", "POST")]
    [InlineData("--by-rule", "code-rule")]
    [InlineData("--by-layer", "tool-result")]
    [InlineData("--by-pipeline", "content")]
    public async Task Text_views_render_requested_server_sections(string option, string expected)
    {
        using var fixture = new Fixture();
        var at = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        var totals = new GainTotals(1, EstimatedSavedTokens: 123);
        fixture.Handler.Report = fixture.Handler.Report with
        {
            History = [new GainHistoryEntry("request-one", at, null, "anthropic", "POST", "code-rule", "tool-result", "content", totals)],
            Daily = [new GainBucket(at, totals)], Weekly = [new GainBucket(at, totals)], Monthly = [new GainBucket(at, totals)],
            Attributions = [new GainAttribution("method", "POST", totals), new GainAttribution("rule", "code-rule", totals), new GainAttribution("layer", "tool-result", totals), new GainAttribution("pipeline", "content", totals)],
            Failures = [new GainFailure(at, "request-one", null, "failed-stage", "fallback")],
            Recalls = [new GainRecall(at, "preview-one", null, "caller", "whole", 10, 3, null)]
        };
        var args = option.Length == 0 ? new[] { "gain" } : new[] { "gain", option };
        var exit = await new CliApplication(fixture.Runtime).RunAsync(args, TestContext.Current.CancellationToken);
        Assert.Equal(0, exit);
        Assert.Contains(expected, fixture.Console.Output);
        Assert.Single(fixture.Handler.Uris);
    }

    [Fact]
    public async Task Csv_escapes_history_values_and_all_filters_are_forwarded()
    {
        using var fixture = new Fixture();
        var at = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        fixture.Handler.Report = fixture.Handler.Report with
        {
            History = [new GainHistoryEntry("request,\"one\"\nnext", at, "/project", "anthropic", "POST", "rule", "tool", "content", new GainTotals(1, EstimatedSavedTokens: -5))]
        };
        var exit = await new CliApplication(fixture.Runtime).RunAsync(
        [
            "gain", "--history", "-f", "csv", "--project-path", "/p a", "--from", "2026-10-01T02:00:00+02:00", "--to", "2026-11-01T00:00:00Z", "--provider", "anthropic", "--method", "POST", "--rule", "rule", "--layer", "tool", "--pipeline",
            "content", "--limit", "7"
        ], TestContext.Current.CancellationToken);
        Assert.Equal(0, exit);
        var csv = fixture.Output.ToString();
        Assert.StartsWith("section,", csv);
        Assert.Contains("\"request,\"\"one\"\"\nnext\"", csv);
        Assert.Contains(",-5,", csv);
        var query = Uri.UnescapeDataString(fixture.Handler.Uris.Single().Query);
        foreach (var part in new[]
                 {
                     "project=/p a", "from=2026-10-01T02:00:00.0000000+02:00", "to=2026-11-01T00:00:00.0000000+00:00", "provider=anthropic", "method=POST", "rule=rule", "layer=tool", "pipeline=content", "historyLimit=7"
                 }) Assert.Contains(part, query);
    }

    [Theory]
    [InlineData("--quota")]
    [InlineData("--tier", "pro")]
    [InlineData("--shadow")]
    [InlineData("--skip-env")]
    public async Task Unsupported_options_are_actionable_errors_before_http(params string[] options)
    {
        using var fixture = new Fixture();
        var exit = await new CliApplication(fixture.Runtime).RunAsync(["gain", .. options], TestContext.Current.CancellationToken);
        Assert.NotEqual(0, exit);
        Assert.Contains("not supported", fixture.Error.ToString());
        Assert.Empty(fixture.Handler.Uris);
        Assert.Equal("", fixture.Output.ToString());
    }

    [Theory]
    [InlineData(false, false, "", 1, 0)]
    [InlineData(false, true, "n", 0, 0)]
    [InlineData(false, true, "y", 0, 1)]
    [InlineData(true, false, "", 0, 1)]
    public async Task Reset_requires_yes_or_explicit_prompt_acceptance(bool yes, bool interactive, string input, int expectedExit, int expectedPosts)
    {
        using var fixture = new Fixture();
        if (input.Length > 0) fixture.Console.Input.PushTextWithEnter(input);
        var runtime = fixture.Runtime with { Interactive = interactive };
        var args = yes ? new[] { "gain", "--reset", "--yes", "--project-path", "/safe", "-f", "json" } : new[] { "gain", "--reset", "--project-path", "/safe", "-f", "json" };
        var exit = await new CliApplication(runtime).RunAsync(args, TestContext.Current.CancellationToken);
        Assert.Equal(expectedExit, exit);
        Assert.Equal(expectedPosts, fixture.Handler.PostBodies.Count);
        if (expectedPosts > 0)
        {
            using var reset = JsonDocument.Parse(fixture.Output.ToString());
            Assert.Equal(7, reset.RootElement.GetProperty("removed").GetInt64());
            Assert.Contains("\"project\":\"/safe\"", fixture.Handler.PostBodies.Single());
        }
    }

    [Theory]
    [InlineData("-f|xml", "format")]
    [InlineData("--endpoint|file:///tmp/proxy", "endpoint")]
    [InlineData("--limit|0", "limit")]
    [InlineData("--limit|1001", "limit")]
    [InlineData("-p|--project-path|/explicit", "project")]
    [InlineData("--from|bad-date", "timestamp")]
    [InlineData("--from|2026-11-01T00:00:00Z|--to|2026-10-01T00:00:00Z", "before")]
    [InlineData("--watch", "interactive")]
    [InlineData("--watch|-f|json", "format")]
    [InlineData("--watch|-f|csv", "format")]
    [InlineData("--yes", "reset")]
    [InlineData("--reset|--watch", "watch")]
    public async Task Invalid_options_fail_before_proxy_calls(string arguments, string message)
    {
        using var fixture = new Fixture();
        var exit = await new CliApplication(fixture.Runtime).RunAsync(["gain", .. arguments.Split('|')], TestContext.Current.CancellationToken);
        Assert.NotEqual(0, exit);
        Assert.Contains(message, fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Handler.Uris);
        Assert.Equal("", fixture.Output.ToString());
    }

    [Fact]
    public async Task Watch_refreshes_on_injected_clock_and_cancellation_exits_without_extra_calls()
    {
        using var fixture = new Fixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new WatchClock();
        var runtime = fixture.Runtime with { Interactive = true, Time = clock };
        var running = new CliApplication(runtime).RunAsync(["gain", "--watch"], cancellation.Token);
        var nextTimer = clock.Scheduled.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        var first = await Task.WhenAny(running, nextTimer);
        Assert.Same(nextTimer, first);
        await nextTimer;
        Assert.Single(fixture.Handler.Uris);
        clock.Advance(TimeSpan.FromSeconds(2));
        await clock.Scheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Handler.Uris.Count);
        cancellation.Cancel();
        Assert.Equal(130, await running);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, fixture.Handler.Uris.Count);
    }

    [Fact]
    public async Task Text_overrides_watch_for_one_static_json_request()
    {
        using var fixture = new Fixture();
        var exit = await new CliApplication(fixture.Runtime).RunAsync(["gain", "--watch", "--text", "-f", "json"], TestContext.Current.CancellationToken);
        Assert.Equal(0, exit);
        Assert.Single(fixture.Handler.Uris);
        using var json = JsonDocument.Parse(fixture.Output.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    internal sealed class WatchClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        public Channel<bool> Scheduled { get; } = Channel.CreateUnbounded<bool>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            Scheduled.Writer.TryWrite(true);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _clock.Advance(elapsed);
        }
    }

    internal sealed class Fixture : IDisposable
    {
        public StringWriter Output { get; } = new();
        public StringWriter Error { get; } = new();
        public TestConsole Console { get; } = new();
        public RecordingHandler Handler { get; } = new();
        public GainRuntime Runtime => new(Console, Output, Error, new ProxyClient(new HttpClient(Handler, false)), "/work/project", false, TimeProvider.System);

        public void Dispose()
        {
            Output.Dispose();
            Error.Dispose();
            Console.Dispose();
            Handler.Dispose();
        }
    }

    internal sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];
        public List<string> PostBodies { get; } = [];
        public GainReport Report { get; set; } = new(1, DateTimeOffset.Parse("2026-10-25T00:00:00Z"), new GainQuery(), new GainMeasurement(), new GainTotals(1, EstimatedSavedTokens: 123), [], [], [], [], [], [], [], []);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            if (request.Method == HttpMethod.Post)
            {
                PostBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new GainResetResult(7, DateTimeOffset.Parse("2026-10-25T00:00:00Z"))) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Report) };
        }
    }
}