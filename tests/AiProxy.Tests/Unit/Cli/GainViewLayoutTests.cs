using AiProxy.Cli;
using AiProxy.Core.Gain;
using Spectre.Console;
using Spectre.Console.Testing;

namespace AiProxy.Tests.Unit.Cli;

public sealed class GainViewLayoutTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-02T12:34:56Z");
    private const string Guid = "3f2b8c1e-9d4a-4e7b-8a61-0c5d7e9f1a2b";
    private const string LongRule = "below-minimum,json.compact,ccr.preview,very.long.rule.name.that.goes.on.and.on.forever";

    private static GainReport Report(bool ansi = false)
    {
        var totals = new GainTotals(1234, EstimatedOriginalTokens: 261_175_524, EstimatedForwardedTokens: 250_443_158, EstimatedSavedTokens: 10_756_350,
            EstimatedRetrievedTokens: 3_000, EstimatedFollowUpTokens: 12_000, FollowUpMeasuredRequests: 5);
        var negative = new GainTotals(10, EstimatedOriginalTokens: 1000, EstimatedForwardedTokens: 1500, EstimatedSavedTokens: -500);
        var recall = new GainRecall(At, Guid, "/Users/someone/RiderProjects/a-very-long-project-name/", "caller", "whole", 289, 100, null);
        return new GainReport(1, At, new GainQuery(), new GainMeasurement(), totals,
            [new GainHistoryEntry(Guid, At, "/Users/someone/RiderProjects/a-very-long-project-name/src", "anthropic", "POST", LongRule, "tool_result", "content", totals)],
            [new GainBucket(At, totals)], [new GainBucket(At, totals)], [new GainBucket(At, totals)],
            [new GainAttribution("rule", LongRule, totals), new GainAttribution("rule", "neg", negative), new GainAttribution("method", "POST", totals)],
            [new GainFailure(At, Guid, null, "compression", new string('e', 200))], [recall], [],
            new GainOtherTraffic(3, 1_234_567_890), [new("harness", "claude", totals), new("harness", "codex", negative), new("harness", "other", new GainTotals(1))]);
    }

    private static string Render(GainSettings settings, GainReport report, int width = 80, bool ansi = false)
    {
        var console = new TestConsole().Width(width);
        if (ansi)
        {
            console.EmitAnsiSequences().Colors(ColorSystem.TrueColor);
        }

        console.Write(new GainView().Render(report, settings));
        return console.Output;
    }

    public static TheoryData<string> Views => new() { "default", "history", "by-rule", "recalls", "all", "failures" };

    private static GainSettings Settings(string view)
    {
        return view switch
        {
            "history" => new GainSettings { History = true },
            "by-rule" => new GainSettings { ByRule = true },
            "recalls" => new GainSettings { Recalls = true },
            "failures" => new GainSettings { Failures = true },
            "all" => new GainSettings { All = true },
            _ => new GainSettings()
        };
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void No_output_line_is_wider_than_the_terminal(string view)
    {
        var output = Render(Settings(view), Report());

        Assert.All(output.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= 80, $"{line.Length}: {line}"));
    }

    [Fact]
    public void Summary_headers_stay_whole_and_each_harness_has_a_row_and_a_total()
    {
        var output = Render(new GainSettings(), Report());

        Assert.Contains("Harness", output, StringComparison.Ordinal);
        Assert.Contains("Saved%", output, StringComparison.Ordinal);
        Assert.Contains("claude", output, StringComparison.Ordinal);
        Assert.Contains("codex", output, StringComparison.Ordinal);
        Assert.Contains("total", output, StringComparison.Ordinal);
        var lines = output.Split('\n');
        var header = Array.FindIndex(lines, static l => l.Contains("Harness", StringComparison.Ordinal) && l.Contains("Net%", StringComparison.Ordinal));
        Assert.Equal(1, lines.Count(static l => l.Contains("Harness", StringComparison.Ordinal) && l.Contains("Net%", StringComparison.Ordinal)));
        var bottom = Array.FindIndex(lines, header, static l => l.StartsWith('╰'));
        var rows = lines[header..bottom];
        foreach (var name in new[] { "claude", "codex", "total" })
        {
            Assert.Single(rows, l => l.StartsWith("│ " + name, StringComparison.Ordinal));
        }
        Assert.Contains("Other traffic: 3 requests, 1.2B bytes", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_without_harness_rows_shows_one_row_named_all()
    {
        var output = Render(new GainSettings(), Report() with { Harnesses = [] });

        Assert.Contains("all", output, StringComparison.Ordinal);
        Assert.DoesNotContain("total", output, StringComparison.Ordinal);
    }

    [Fact]
    public void History_shows_short_ids_the_last_folder_and_a_short_time()
    {
        var output = Render(new GainSettings { History = true }, Report());

        Assert.DoesNotContain(Guid, output, StringComparison.Ordinal);
        Assert.Contains(Guid[^8..], output, StringComparison.Ordinal);
        Assert.Contains("11-02 12:34:56", output, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/someone", output, StringComparison.Ordinal);
    }

    [Fact]
    public void By_rule_explains_that_rows_overlap()
    {
        var output = Render(new GainSettings { ByRule = true }, Report());

        Assert.Contains("counts under each", output.Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Net_cells_are_red_when_negative_and_green_when_positive()
    {
        var report = Report();
        var negative = Render(new GainSettings { ByRule = true }, report with { Attributions = [new("rule", "neg", new GainTotals(1, EstimatedSavedTokens: -5))] }, ansi: true);
        var positive = Render(new GainSettings { ByRule = true }, report with { Attributions = [new("rule", "pos", new GainTotals(1, EstimatedSavedTokens: 5))] }, ansi: true);

        Assert.Contains("\u001b[38;5;9m-5", negative, StringComparison.Ordinal);
        Assert.Contains("\u001b[38;5;2m5", positive, StringComparison.Ordinal);
    }

    [Fact]
    public void Markup_characters_in_data_render_literally()
    {
        var report = Report() with
        {
            Attributions = [new("rule", "x[red]", new GainTotals(1, EstimatedSavedTokens: 5))],
            History = [new GainHistoryEntry(Guid, At, "/a/b[c", "anthropic", "POST", "r", "l", "p", new GainTotals(1))],
            Failures = [new GainFailure(At, Guid, null, "s", "a[b")]
        };

        var output = Render(new GainSettings { ByRule = true, History = true, Failures = true }, report);

        Assert.Contains("x[red]", output, StringComparison.Ordinal);
        Assert.Contains("b[c", output, StringComparison.Ordinal);
        Assert.Contains("a[b", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_cell_is_grey_and_a_negative_net_with_zero_saved_is_red()
    {
        var harness = new GainAttribution("harness", "h", new GainTotals(1, EstimatedSavedTokens: 0, EstimatedToolTokens: 7));
        var output = Render(new GainSettings(), Report() with { Harnesses = [harness] }, ansi: true);

        Assert.Contains("\u001b[38;5;9m-7", output, StringComparison.Ordinal);
        Assert.Contains("\u001b[38;5;8m0", output, StringComparison.Ordinal);
    }
}
