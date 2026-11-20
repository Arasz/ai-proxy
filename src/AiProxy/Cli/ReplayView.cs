using System.Globalization;
using System.Text.Json;
using AiProxy.Core.Replay;
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
            source = settings.Source,
            rowsRead = report.RowsRead,
            rowsUndecodable = report.RowsUndecodable,
            rowsNotJson = report.RowsNotJson,
            rowsNoToolRegion = report.RowsNoToolRegion,
            rowsMisaligned = report.RowsMisaligned,
            rowsDuplicate = report.RowsDuplicate,
            rowsReplayed = report.RowsReplayed,
            rowsWithPreview = report.RowsWithPreview,
            captureRows = run.CaptureRows,
            gainRequests = run.GainRequests,
            savedTokens = report.SavedTokens,
            kinds = report.Kinds.Select(static kind => new
            {
                kind = kind.Kind.ToString(),
                blocksSeen = kind.BlocksSeen,
                blocksPreviewed = kind.BlocksPreviewed,
                originalTokens = kind.OriginalTokens,
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
        rows.AddRow("duplicate", Number(report.RowsDuplicate));
        rows.AddRow("replayed", Number(report.RowsReplayed));
        rows.AddRow("with a preview", Number(report.RowsWithPreview));
        rows.AddRow("capture rows read", Number(run.CaptureRows));
        rows.AddRow("gain requests (same window)", gain);
        console.Write(rows);

        var kinds = new Table().AddColumn("Kind");
        foreach (var heading in new[] { "Seen", "Previewed", "Original", "Forwarded", "Saved" })
        {
            kinds.AddColumn(new TableColumn(heading).RightAligned());
        }

        foreach (var kind in report.Kinds)
        {
            kinds.AddRow(kind.Kind.ToString(), Number(kind.BlocksSeen), Number(kind.BlocksPreviewed), Number(kind.OriginalTokens), Number(kind.ForwardedTokens), Number(kind.SavedTokens));
        }

        console.Write(kinds);
        var sends = report.Previews.Sum(static preview => preview.Sends);
        console.WriteLine($"previews: {Number(report.Previews.Count)} ids sent {Number(sends)} times, saving {Number(report.SavedTokens)} estimated tokens in all.");
    }

    private static string Number(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
