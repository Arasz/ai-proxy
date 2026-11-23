using AiProxy.Cli;
using AiProxy.Client;
using AiProxy.Core.Gain;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console.Testing;

namespace AiProxy.Tests.Unit.Gain;

/// <summary>The live view redraws only when what it shows changed.</summary>
public sealed class GainWatchTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-02T12:00Z");

    private static GainReport Report(params GainHistoryEntry[] requests) => GainReportBuilder.Build(new GainQuery(), At, requests, [], []);

    private static GainHistoryEntry Entry(string id, long saved) =>
        new(id, At, "/p", "anthropic", "POST", "log", "tool_result", "content", new GainTotals(1, 100, 50, 100, 50, 30, 20, saved));

    [Fact]
    public void A_report_that_differs_only_in_its_generated_time_is_not_a_change()
    {
        var shown = Report(Entry("a", 10));

        Assert.False(GainWatch.Changed(shown, Report(Entry("a", 10)) with { GeneratedAt = At.AddSeconds(2) }));
    }

    [Fact]
    public void A_new_request_or_a_different_total_is_a_change()
    {
        var shown = Report(Entry("a", 10));

        Assert.True(GainWatch.Changed(shown, Report(Entry("a", 10), Entry("b", 10))));
        Assert.True(GainWatch.Changed(shown, Report(Entry("a", 11))));
    }

    [Fact]
    public async Task The_view_is_drawn_once_even_when_no_poll_brings_a_change()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.AlternateBuffer = true;
        var clock = new FakeTimeProvider(At);
        var initial = Report(Entry("a", 10));
        var client = new SameReport(initial, clock);
        var runtime = new GainRuntime(console, new StringWriter(), new StringWriter(), client, "/work", true, clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var watch = new GainWatch(runtime).RunAsync(client, new ProxyApi("http://127.0.0.1:1", null, "token"), new GainQuery(), new GainSettings(), initial, stop.Token);
        for (var polls = 0; client.Calls < 2 && polls < 500; polls++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var drawn = console.Output;
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch);

        Assert.True(client.Calls >= 2, $"polls {client.Calls}");
        Assert.Contains("gain (Ctrl+C exits)", drawn);
    }

    /// <summary>Returns the same report on every poll, with only its generated time moved on.</summary>
    private sealed class SameReport(GainReport report, TimeProvider clock) : IProxyClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<GainReport> GetGainAsync(ProxyApi api, GainQuery query, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(report with { GeneratedAt = clock.GetUtcNow() });
        }

        public Task<RetrievalResponse> RetrieveAsync(ProxyApi api, RetrievalRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GainResetResult> ResetGainAsync(ProxyApi api, GainQuery query, string caller, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
