using AiProxy.Cli;
using AiProxy.Core.Content;
using AiProxy.Core.Replay;
using AiProxy.Replay;
using Spectre.Console.Testing;

namespace AiProxy.Tests.Unit.Cli;

public sealed class ReplayViewLayoutTests
{
    private static string[] Render()
    {
        var report = new ReplayReport();
        foreach (var kind in Enum.GetValues<ContentType>())
        {
            report.Add(new ReplayRow(ReplayOutcome.Replayed, [new ReplayBlock(kind, 235_400_000, 100_000_000, 12_300_000, "preview")], "anthropic", "api.anthropic.com"));
        }

        var console = new TestConsole().Width(TableLayout.Width);
        new ReplayView("/home", new ReplaySettings(), new ReplayRun(report, 1_234_567, 1_234_567)).Render(console);
        return TableLayout.Lines(console.Output);
    }

    [Fact]
    public void Every_kind_row_takes_one_line_at_80_columns_with_live_size_numbers_and_whole_headers()
    {
        var lines = Render();

        var table = TableLayout.Table(lines, "Kind", "Reducers and Previews");
        Assert.Equal(1 + Enum.GetValues<ContentType>().Length, table.Length);
        Assert.All(lines, line => Assert.True(line.Length <= TableLayout.Width, $"{line.Length}: {line}"));
        Assert.Equal(["Kind", "Seen", "Previewed", "Skipped", "Original", "Reducers", "Previews", "Forwarded"], TableLayout.Cells(table[0]));
        var rows = table[1..].Select(TableLayout.Cells).ToArray();
        Assert.Equal(Enum.GetValues<ContentType>().Select(static kind => kind.ToString()).Order(), rows.Select(static cells => cells[0]).Order());
        Assert.All(rows, cells => Assert.Equal(["1", "1", "0", "235.4M", "135.4M", "87.7M", "12.3M"], cells[1..]));
    }
}
