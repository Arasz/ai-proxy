using System.Globalization;
using AiProxy.Core.Gain;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AiProxy.Cli;

/// <summary>Renders server-computed statistics without recomputing accounting.</summary>
public sealed class GainView
{
    private static readonly (decimal Unit, string Name)[] Units = [(1_000m, "K"), (1_000_000m, "M"), (1_000_000_000m, "B")];

    public IRenderable Render(GainReport report, GainSettings settings)
    {
        var views = new List<IRenderable>
        {
            new Text("estimated initial-request token reductions (tokens)\n"),
            Table(["Requests", "Original", "Forwarded", "Saved", "Retrieved", "Tool", "Net", "Saved %", "Net %"],
            [
                [Compact(report.Summary.Requests), Compact(report.Summary.EstimatedOriginalTokens), Compact(report.Summary.EstimatedForwardedTokens), Compact(report.Summary.EstimatedSavedTokens), Compact(report.Summary.EstimatedRetrievedTokens),
                    Compact(report.Summary.EstimatedToolTokens), Compact(report.Summary.EstimatedNetTokens), Percent(report.Summary.SavedPercent), Percent(report.Summary.NetPercent)]
            ]),
            new Text($"Method: {report.Measurement.Kind}, {report.Measurement.CharactersPerToken.ToString(CultureInfo.InvariantCulture)} chars/token. Net billed savings unavailable.\n")
        };
        if (report.OtherTraffic is { } other)
        {
            views.Add(new Text($"Other traffic: {Number(other.Requests)} requests, {Number(other.Bytes)} bytes (telemetry and non-model calls; not counted above).\n"));
        }

        if ((settings.Project || settings.ProjectPath is not null) && report.Summary.Requests == 0)
        {
            views.Add(new Text($"No requests for this project. Traffic is attributed only when the client sends the {GainApi.ProjectHeader} header; launch the agent with `ai-proxy harness` to set it.\n"));
        }
        if (settings.History)
        {
            views.Add(new Text("History\n"));
            views.Add(Table(["UTC", "Request", "Project", "Provider", "Pipeline", "Saved tokens"],
                report.History.Select(static row => new[] { Stamp(row.At), row.Id, row.Project ?? GainLabels.Unknown, row.Provider ?? GainLabels.Unknown, row.Pipeline, Compact(row.Totals.EstimatedSavedTokens) })));
        }

        if (settings.All || settings.Daily)
        {
            AddBuckets(views, "Daily (UTC)", report.Daily);
        }

        if (settings.All || settings.Weekly)
        {
            AddBuckets(views, "Weekly (Monday UTC)", report.Weekly);
        }

        if (settings.All || settings.Monthly)
        {
            AddBuckets(views, "Monthly (UTC)", report.Monthly);
        }

        if (settings.Graph)
        {
            views.Add(new Text("Daily savings (estimated tokens)\n"));
            var maximum = report.Daily.Count == 0 ? 1m : Math.Max(1m, report.Daily.Max(row => Math.Abs((decimal)row.Totals.EstimatedSavedTokens)));
            foreach (var row in report.Daily)
            {
                var value = row.Totals.EstimatedSavedTokens;
                var length = (int)(Math.Abs((decimal)value) / maximum * 30);
                views.Add(new Text($"{row.Start:yyyy-MM-dd} {Compact(value),10} {new string(value < 0 ? '<' : '#', length)}\n"));
            }

            if (report.Daily.Count == 0)
            {
                views.Add(new Text("No recorded days.\n"));
            }
        }

        var dimensions = new List<string>();
        if (settings.ByMethod || settings.Pipeline is not null)
        {
            dimensions.Add("method");
        }

        if (settings.ByRule || settings.Pipeline is not null)
        {
            dimensions.Add("rule");
        }

        if (settings.ByLayer || settings.Pipeline is not null)
        {
            dimensions.Add("layer");
        }

        if (settings.ByPipeline || settings.Pipeline is not null)
        {
            dimensions.Add("pipeline");
        }

        foreach (var dimension in dimensions)
        {
            views.Add(new Text($"By {dimension}\n"));
            views.Add(Table(["Name", "Requests", "Saved tokens", "Net tokens", "Recalls"], report.Attributions.Where(row => row.Dimension == dimension)
                .Select(static row => new[] { row.Name, Compact(row.Totals.Requests), Compact(row.Totals.EstimatedSavedTokens), Compact(row.Totals.EstimatedNetTokens), Compact(row.Totals.Recalls) })));
        }

        if (settings.Failures)
        {
            views.Add(new Text("Failures\n"));
            views.Add(Table(["UTC", "Request", "Stage", "Error"], report.Failures.Select(static row => new[] { Stamp(row.At), row.RequestId, row.Stage, row.Error })));
        }

        if (settings.Recalls)
        {
            views.Add(new Text("Recalls (caller attribution; origins may be unknown)\n"));
            views.Add(Table(["UTC", "Preview", "Project", "Mode", "Tokens", "Error"],
                report.Recalls.Select(static row => new[] { Stamp(row.At), row.PreviewId, row.Project ?? GainLabels.Unknown, row.Mode, Compact(row.EstimatedTokens), row.Error ?? "" })));
        }

        return new Rows(views);
    }

    private static void AddBuckets(List<IRenderable> views, string title, IReadOnlyList<GainBucket> buckets)
    {
        views.Add(new Text($"{title}\n"));
        views.Add(Table(["Start (UTC)", "Requests", "Saved tokens", "Retrieved tokens", "Net tokens"],
            buckets.Select(static row => new[] { Stamp(row.Start), Compact(row.Totals.Requests), Compact(row.Totals.EstimatedSavedTokens), Compact(row.Totals.EstimatedRetrievedTokens), Compact(row.Totals.EstimatedNetTokens) })));
    }

    private static Table Table(string[] columns, IEnumerable<string[]> rows)
    {
        var table = new Table().Border(TableBorder.Rounded);
        foreach (var column in columns)
        {
            table.AddColumn(new TableColumn(new Text(column)));
        }

        var any = false;
        foreach (var row in rows)
        {
            table.AddRow(row.Select(value => (IRenderable)new Text(value)).ToArray());
            any = true;
        }

        if (!any)
        {
            table.AddRow(columns.Select((_, index) => (IRenderable)new Text(index == 0 ? "No records" : "")).ToArray());
        }

        return table;
    }

    /// <summary>A table cell: exact below ten thousand, otherwise one decimal of K, M or B so wide counts never wrap.</summary>
    internal static string Compact(long value)
    {
        if (Math.Abs(value) < 10_000)
        {
            return Number(value);
        }

        var scaled = 0m;
        var suffix = "";
        foreach (var (unit, name) in Units)
        {
            scaled = Math.Round(value / unit, 1, MidpointRounding.AwayFromZero);
            suffix = name;
            if (Math.Abs(scaled) < 1_000m)
            {
                break;
            }
        }

        return scaled.ToString("0.0", CultureInfo.InvariantCulture) + suffix;
    }

    private static string Number(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string Percent(decimal value)
    {
        return value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    private static string Stamp(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}