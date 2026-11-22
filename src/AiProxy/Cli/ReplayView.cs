using System.Globalization;
using System.Text.Json;
using AiProxy.Replay;
using Spectre.Console;

namespace AiProxy.Cli;

/// <summary>Prints a replay as denominators first, then a table per content kind.</summary>
/// <param name="home">The data directory that was read.</param>
/// <param name="settings">What was asked for.</param>
/// <param name="run">What the replay found.</param>
public sealed class ReplayView(string home, ReplaySettings settings, ReplayRun run)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The result as JSON, for the tuning step to read.</summary>
    public string Json()
    {
        var report = run.Report;
        return JsonSerializer.Serialize(new
        {
            home,
            since = settings.SinceTime,
            minimum = settings.Minimum,
            budgetShare = settings.BudgetShare,
            minimumSaving = settings.MinimumSaving,
            skipUnder = settings.SkipUnder,
            source = settings.Source,
            includeEncoded = settings.IncludeEncoded,
            coverage = run.Coverage,
            rowsRead = report.RowsRead,
            rowsUndecodable = report.RowsUndecodable,
            rowsNotJson = report.RowsNotJson,
            rowsNoToolRegion = report.RowsNoToolRegion,
            rowsMisaligned = report.RowsMisaligned,
            rowsDuplicate = report.RowsDuplicate,
            rowsEncoded = report.RowsEncoded,
            rowsReplayed = report.RowsReplayed,
            rowsWithPreview = report.RowsWithPreview,
            captureRows = run.CaptureRows,
            gainRequests = run.GainRequests,
            reducerSavedTokens = report.ReducerSavedTokens,
            previewSavedTokens = report.PreviewSavedTokens,
            blocksSkipped = report.BlocksSkipped,
            savedTokens = report.SavedTokens,
            hosts = report.Hosts.Select(static host => new { host = host.Host, rows = host.Rows }),
            kinds = report.Kinds.Select(static kind => new
            {
                kind = kind.Kind.ToString(),
                blocksSeen = kind.BlocksSeen,
                blocksPreviewed = kind.BlocksPreviewed,
                blocksSkipped = kind.BlocksSkipped,
                originalTokens = kind.OriginalTokens,
                reducerSavedTokens = kind.ReducerSavedTokens,
                previewSavedTokens = kind.PreviewSavedTokens,
                forwardedTokens = kind.ForwardedTokens,
                savedTokens = kind.SavedTokens
            }),
            previews = report.Previews.Select(static preview => new { id = preview.Id, sends = preview.Sends, savedTokens = preview.SavedTokens })
        }, JsonOptions);
    }

    /// <summary>Writes the tables to the console.</summary>
    /// <param name="console">Where to write.</param>
    public void Render(IAnsiConsole console)
    {
        var report = run.Report;
        var gain = run.GainRequests?.ToString(CultureInfo.InvariantCulture) ?? "no ledger";
        console.WriteLine($"replay of {home}: minimum {settings.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "default"}, source {settings.Source}, since {settings.SinceTime?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "start"}");
        var rows = new Table().AddColumn("Rows").AddColumn(new TableColumn("Count").RightAligned());
        rows.AddRow("read", Number(report.RowsRead));
        rows.AddRow("undecodable", Number(report.RowsUndecodable));
        rows.AddRow("not JSON", Number(report.RowsNotJson));
        rows.AddRow("no tool region", Number(report.RowsNoToolRegion));
        rows.AddRow("misaligned", Number(report.RowsMisaligned));
        rows.AddRow("duplicate (body already counted)", Number(report.RowsDuplicate));
        rows.AddRow("encoded (left out)", Number(report.RowsEncoded));
        rows.AddRow("replayed", Number(report.RowsReplayed));
        rows.AddRow("with a preview", Number(report.RowsWithPreview));
        rows.AddRow("capture rows read", Number(run.CaptureRows));
        rows.AddRow("gain requests (same window)", gain);
        console.Write(rows);
        if (run.Coverage == ReplayRun.ContentOnly)
        {
            console.WriteLine("warning: coverage is content-only. content.db holds only the requests the pipeline changed, so this sample misses requests a lower floor would newly preview. Turn capture on for a full sample.");
        }


        var kinds = new Table().Border(TableBorder.Minimal).AddColumn(new TableColumn("Kind") { Padding = new Padding(0, 0, 0, 0), NoWrap = true });
        foreach (var heading in new[] { "Seen", "Previewed", "Skipped", "Original", "Reducers", "Previews", "Forwarded" })
        {
            kinds.AddColumn(new TableColumn(heading) { Padding = new Padding(0, 0, 0, 0), NoWrap = true, Alignment = Justify.Right });
        }

        foreach (var kind in report.Kinds)
        {
            kinds.AddRow(kind.Kind.ToString(), GainView.Compact(kind.BlocksSeen), GainView.Compact(kind.BlocksPreviewed), GainView.Compact(kind.BlocksSkipped), GainView.Compact(kind.OriginalTokens), GainView.Compact(kind.ReducerSavedTokens), GainView.Compact(kind.PreviewSavedTokens), GainView.Compact(kind.ForwardedTokens));
        }

        console.Write(kinds);
        console.WriteLine("Reducers and Previews are the estimated tokens each one removed; Forwarded is what is left.");
        var sends = report.Previews.Sum(static preview => preview.Sends);
        console.WriteLine($"previews: {Number(report.Previews.Count)} ids sent {Number(sends)} times, saving {Number(report.PreviewSavedTokens)} estimated tokens in all on top of the reducers.");
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
