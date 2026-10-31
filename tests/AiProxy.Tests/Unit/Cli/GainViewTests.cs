using System.Reflection;
using AiProxy.Cli;
using AiProxy.Core.Gain;
using Spectre.Console.Cli;

namespace AiProxy.Tests.Unit.Cli;

public sealed class GainViewTests
{
    private static readonly string[] RtkOnlyFlags = ["--quota", "--tier", "--shadow", "--skip-env"];

    [Fact]
    public async Task The_summary_table_shows_net_and_percentages_beside_the_gross_figure()
    {
        using var fixture = new GainCommandTests.Fixture();
        fixture.Console.Profile.Width = 240;
        fixture.Handler.Report = fixture.Handler.Report with
        {
            Summary = new GainTotals(3, EstimatedOriginalTokens: 10_000, EstimatedSavedTokens: 1_000, EstimatedRetrievedTokens: 300, EstimatedToolTokens: 50)
        };

        var exit = await new CliApplication(fixture.Runtime).RunAsync(["gain"], TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        var output = fixture.Console.Output;
        Assert.Contains("(tokens)", output, StringComparison.Ordinal);
        Assert.Contains("Saved %", output, StringComparison.Ordinal);
        Assert.Contains("Net %", output, StringComparison.Ordinal);
        Assert.Contains("650", output, StringComparison.Ordinal);
        Assert.Contains("10.0%", output, StringComparison.Ordinal);
        Assert.Contains("6.5%", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_traffic_is_shown_apart_from_the_savings_row_in_the_table_and_appended_to_csv_and_json()
    {
        using var fixture = new GainCommandTests.Fixture();
        fixture.Console.Profile.Width = 240;
        fixture.Handler.Report = fixture.Handler.Report with { OtherTraffic = new GainOtherTraffic(179, 123_456) };

        await new CliApplication(fixture.Runtime).RunAsync(["gain"], TestContext.Current.CancellationToken);
        Assert.Contains("Other traffic: 179 requests, 123,456 bytes", fixture.Console.Output, StringComparison.Ordinal);

        await new CliApplication(fixture.Runtime).RunAsync(["gain", "-f", "csv"], TestContext.Current.CancellationToken);
        var csv = fixture.Output.ToString();
        Assert.StartsWith("section,at,name,project,provider,requests,estimated_saved_tokens,estimated_retrieved_tokens,error,estimated_tool_tokens,estimated_net_tokens,bytes\r\n", csv, StringComparison.Ordinal);
        Assert.Contains("other,,other-traffic,,,179,0,0,,0,0,123456", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_from_an_older_proxy_shows_no_other_traffic_line()
    {
        using var fixture = new GainCommandTests.Fixture();
        fixture.Console.Profile.Width = 240;

        await new CliApplication(fixture.Runtime).RunAsync(["gain"], TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Other traffic", fixture.Console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_negative_net_is_printed_with_its_sign()
    {
        using var fixture = new GainCommandTests.Fixture();
        fixture.Console.Profile.Width = 240;
        fixture.Handler.Report = fixture.Handler.Report with
        {
            Summary = new GainTotals(1, EstimatedOriginalTokens: 1_000, EstimatedSavedTokens: 100, EstimatedRetrievedTokens: 80, EstimatedToolTokens: 50)
        };

        await new CliApplication(fixture.Runtime).RunAsync(["gain"], TestContext.Current.CancellationToken);

        Assert.Contains("-30", fixture.Console.Output, StringComparison.Ordinal);
        Assert.Contains("-3.0%", fixture.Console.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(9_999, "9,999")]
    [InlineData(10_000, "10.0K")]
    [InlineData(523_811, "523.8K")]
    [InlineData(999_949, "999.9K")]
    [InlineData(999_950, "1.0M")]
    [InlineData(261_175_524, "261.2M")]
    [InlineData(999_950_000, "1.0B")]
    [InlineData(2_500_000_000, "2.5B")]
    [InlineData(-30, "-30")]
    [InlineData(-12_345, "-12.3K")]
    public void Table_numbers_are_exact_below_ten_thousand_and_compact_above(long value, string expected)
    {
        Assert.Equal(expected, GainView.Compact(value));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    public async Task The_summary_keeps_every_number_on_one_line_at_common_terminal_widths(int width)
    {
        using var fixture = new GainCommandTests.Fixture();
        fixture.Console.Profile.Width = width;
        fixture.Handler.Report = fixture.Handler.Report with { Summary = LiveSummary };

        await new CliApplication(fixture.Runtime).RunAsync(["gain"], TestContext.Current.CancellationToken);

        var output = fixture.Console.Output;
        foreach (var value in new[] { "4,593", "261.2M", "250.4M", "10.8M", "523.8K", "24.0K", "10.2M", "4.1%", "3.9%" })
        {
            Assert.Contains(value, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_summary_fits_a_hundred_column_terminal_with_one_line_per_row()
    {
        using var fixture = new GainCommandTests.Fixture();
        fixture.Console.Profile.Width = 100;
        fixture.Handler.Report = fixture.Handler.Report with { Summary = LiveSummary };

        await new CliApplication(fixture.Runtime).RunAsync(["gain"], TestContext.Current.CancellationToken);

        var table = fixture.Console.Output.Split('\n').SkipWhile(line => !line.StartsWith('╭')).TakeWhile(line => !line.StartsWith("Method", StringComparison.Ordinal)).Where(line => line.Length > 0).ToList();
        Assert.Equal(5, table.Count);
    }

    private static readonly GainTotals LiveSummary = new(4_593, EstimatedOriginalTokens: 261_175_524, EstimatedForwardedTokens: 250_443_158, EstimatedSavedTokens: 10_756_350, EstimatedRetrievedTokens: 523_811, EstimatedToolTokens: 23_976);

    [Fact]
    public async Task Csv_and_json_keep_raw_numbers_and_add_the_net_columns()
    {
        using var fixture = new GainCommandTests.Fixture();
        fixture.Console.Profile.Width = 240;
        fixture.Handler.Report = fixture.Handler.Report with
        {
            Summary = new GainTotals(1, EstimatedOriginalTokens: 10_000, EstimatedSavedTokens: 1_234_567, EstimatedRetrievedTokens: 300, EstimatedToolTokens: 50)
        };

        await new CliApplication(fixture.Runtime).RunAsync(["gain", "-f", "csv"], TestContext.Current.CancellationToken);
        var csv = fixture.Output.ToString();

        Assert.Contains("estimated_tool_tokens,estimated_net_tokens", csv, StringComparison.Ordinal);
        Assert.Contains(",1234567,300,,50,1234217", csv, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gain|--project", true)]
    [InlineData("gain|--project-path|/p", true)]
    [InlineData("gain", false)]
    public async Task An_empty_project_prints_one_hint_naming_the_header_and_the_harness(string arguments, bool expectHint)
    {
        using var fixture = new GainCommandTests.Fixture();
        fixture.Console.Profile.Width = 240;
        fixture.Handler.Report = fixture.Handler.Report with { Summary = new GainTotals() };

        await new CliApplication(fixture.Runtime).RunAsync(arguments.Split('|'), TestContext.Current.CancellationToken);

        var output = fixture.Console.Output;
        Assert.Equal(expectHint, output.Contains("X-Ai-Proxy-Project", StringComparison.Ordinal));
        Assert.Equal(expectHint, output.Contains("ai-proxy harness", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_with_requests_gets_no_hint()
    {
        using var fixture = new GainCommandTests.Fixture();
        fixture.Console.Profile.Width = 240;

        await new CliApplication(fixture.Runtime).RunAsync(["gain", "--project"], TestContext.Current.CancellationToken);

        Assert.DoesNotContain("X-Ai-Proxy-Project", fixture.Console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_hides_the_unavailable_flags_and_keeps_the_real_ones()
    {
        using var fixture = new GainCommandTests.Fixture();
        fixture.Console.Profile.Width = 240;

        var exit = await new CliApplication(fixture.Runtime).RunAsync(["gain", "--help"], TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        var help = fixture.Console.Output;
        Assert.DoesNotContain("Unavailable", help, StringComparison.Ordinal);
        foreach (var flag in RtkOnlyFlags)
        {
            Assert.DoesNotContain(flag, help, StringComparison.Ordinal);
        }

        Assert.Contains("--by-rule", help, StringComparison.Ordinal);
        Assert.Contains("--reset", help, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_visible_flag_has_a_description()
    {
        var undescribed = typeof(GainSettings).GetProperties()
            .Select(static property => (property.Name, Option: property.GetCustomAttribute<CommandOptionAttribute>(), Description: property.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()))
            .Where(static row => row.Option is { IsHidden: false } && string.IsNullOrWhiteSpace(row.Description?.Description))
            .Select(static row => row.Name)
            .ToArray();

        Assert.Empty(undescribed);
    }

    [Theory]
    [InlineData("--quota")]
    [InlineData("--shadow")]
    [InlineData("--skip-env")]
    public async Task Hidden_flags_still_parse_and_explain_why_they_are_unsupported(string flag)
    {
        using var fixture = new GainCommandTests.Fixture();
        fixture.Console.Profile.Width = 240;

        var exit = await new CliApplication(fixture.Runtime).RunAsync(["gain", flag], TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exit);
        Assert.Contains("not supported", fixture.Error.ToString(), StringComparison.Ordinal);
    }
}
