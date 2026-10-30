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
        Assert.Contains("Net tokens", output, StringComparison.Ordinal);
        Assert.Contains("Saved %", output, StringComparison.Ordinal);
        Assert.Contains("Net %", output, StringComparison.Ordinal);
        Assert.Contains("650", output, StringComparison.Ordinal);
        Assert.Contains("10.0%", output, StringComparison.Ordinal);
        Assert.Contains("6.5%", output, StringComparison.Ordinal);
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

    [Fact]
    public async Task Table_numbers_carry_thousands_separators()
    {
        using var fixture = new GainCommandTests.Fixture();
        fixture.Console.Profile.Width = 240;
        fixture.Handler.Report = fixture.Handler.Report with
        {
            Summary = new GainTotals(12_345, EstimatedOriginalTokens: 9_876_543, EstimatedSavedTokens: 1_234_567)
        };

        await new CliApplication(fixture.Runtime).RunAsync(["gain"], TestContext.Current.CancellationToken);

        Assert.Contains("1,234,567", fixture.Console.Output, StringComparison.Ordinal);
        Assert.Contains("9,876,543", fixture.Console.Output, StringComparison.Ordinal);
        Assert.Contains("12,345", fixture.Console.Output, StringComparison.Ordinal);
    }

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
