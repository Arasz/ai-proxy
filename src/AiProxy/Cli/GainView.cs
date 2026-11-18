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
            new Text("estimated initial-request token reductions (tokens)\n")
        };
        AddTotals(views, report);
        var toolNote = report.Summary.EstimatedToolTokens > 0 ? $" Injected tool definition: {Number(report.Summary.EstimatedToolTokens)} tokens." : "";
        views.Add(new Text($"Method: {report.Measurement.Kind}, {report.Measurement.CharactersPerToken.ToString(CultureInfo.InvariantCulture)} chars/token. Net billed savings unavailable.{toolNote}\n"));
        if (report.Summary.FollowUpMeasuredRequests > 0)
        {
            views.Add(new Text($"Billed balance (all requests): {Number(report.Summary.EstimatedBilledBalanceTokens)} tokens (estimated from sizes sent, not provider-billed: original - forwarded - follow-up; follow-ups measured on {Number(report.Summary.FollowUpMeasuredRequests)} of {Number(report.Summary.Requests)} requests).\n"));
        }
        if ((settings.Project || settings.ProjectPath is not null) && report.Summary.Requests == 0)
        {
            views.Add(new Text($"No requests for this project. Traffic is attributed only when the client sends the {GainApi.ProjectHeader} header or the proxy credential; launch the agent with `ai-proxy harness` to set both.\n"));
        }
        if (settings.History)
        {
            views.Add(Title("History"));
            views.Add(Table([new("UTC"), new("Request"), new("Project"), new("Provider"), new("Pipeline"), new("Saved", true)],
                report.History.Select(static row => new[] { Plain(ShortStamp(row.At)), Plain(Tail(row.Id)), Plain(Folder(row.Project)), Plain(row.Provider ?? GainLabels.Unknown), Plain(row.Pipeline), Signed(row.Totals.EstimatedSavedTokens) })));
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
            views.Add(Title("Daily savings (estimated tokens)"));
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
            dimensions.Add(GainLabels.Dimensions.Rule);
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
            views.Add(Title($"By {dimension}"));
            views.Add(Table([new("Name"), new("Req", true), new("Saved", true), new("Net", true), new("Recalls", true)], report.Attributions.Where(row => row.Dimension == dimension)
                .Select(static row => new[] { Plain(row.Name), Plain(Compact(row.Totals.Requests)), Signed(row.Totals.EstimatedSavedTokens), Signed(row.Totals.EstimatedNetTokens), Plain(Compact(row.Totals.Recalls)) })));
            if (dimension == GainLabels.Dimensions.Rule)
            {
                views.Add(new Markup("[dim]A request that used several rules counts under each, so rows overlap and do not add up to the total.[/]\n"));
            }
        }

        if (settings.Failures)
        {
            views.Add(Title("Failures"));
            views.Add(Table([new("UTC"), new("Request"), new("Stage"), new("Error")],
                report.Failures.Select(static row => new[] { Plain(ShortStamp(row.At)), Plain(Tail(row.RequestId)), Plain(row.Stage), Plain(row.Error) })));
        }

        if (settings.Recalls)
        {
            views.Add(Title("Recalls (caller attribution; origins may be unknown)"));
            views.Add(Table([new("UTC"), new("Preview"), new("Project"), new("Mode"), new("Tokens", true), new("Error")],
                report.Recalls.Select(static row => new[] { Plain(ShortStamp(row.At)), Plain(Tail(row.PreviewId)), Plain(Folder(row.Project)), Plain(row.Mode), Plain(Compact(row.EstimatedTokens)), Plain(row.Error ?? "") })));
        }

        return new Rows(views);
    }

    /// <summary>One summary table, one row per harness plus a total row; a report without harness rows gets one row named all.</summary>
    private static void AddTotals(List<IRenderable> views, GainReport report)
    {
        var harnesses = report.Harnesses ?? [];
        var rows = harnesses.Count == 0
            ? [SummaryRow("all", report.Summary)]
            : harnesses.Select(static harness => SummaryRow(harness.Name, harness.Totals)).ToList();
        if (harnesses.Count > 1)
        {
            rows.Add(SummaryRow("total", report.Summary));
        }

        views.Add(Table([new("Harness"), new("Req", true), new("Orig", true), new("Fwd", true), new("Saved", true), new("Recall", true),
            new("Follow", true), new("Net", true), new("Saved%", true), new("Net%", true)], rows, tight: true));
        if (report.OtherTraffic is { } other)
        {
            views.Add(new Text($"Other traffic: {Number(other.Requests)} requests, {Compact(other.Bytes)} bytes (telemetry and non-model calls; not counted above).\n"));
        }
    }

    private static IRenderable[] SummaryRow(string name, GainTotals totals)
    {
        return
        [
            Plain(name), Plain(Compact(totals.Requests)), Plain(Compact(totals.EstimatedOriginalTokens)), Plain(Compact(totals.EstimatedForwardedTokens)),
            Signed(totals.EstimatedSavedTokens), Plain(Compact(totals.EstimatedRetrievedTokens)), Plain(Compact(totals.EstimatedFollowUpTokens)),
            Signed(totals.EstimatedNetTokens), Signed(totals.SavedPercent, Percent(totals.SavedPercent)), Signed(totals.NetPercent, Percent(totals.NetPercent))
        ];
    }

    private static void AddBuckets(List<IRenderable> views, string title, IReadOnlyList<GainBucket> buckets)
    {
        views.Add(Title(title));
        views.Add(Table([new("Start (UTC)"), new("Req", true), new("Saved", true), new("Retrieved", true), new("Net", true)],
            buckets.Select(static row => new[] { Plain(Stamp(row.Start)), Plain(Compact(row.Totals.Requests)), Signed(row.Totals.EstimatedSavedTokens), Plain(Compact(row.Totals.EstimatedRetrievedTokens)), Signed(row.Totals.EstimatedNetTokens) })));
    }

    /// <summary>A table column; numbers align right.</summary>
    private sealed record Column(string Header, bool Right = false);

    private static Markup Title(string text)
    {
        return new Markup($"[bold]{Markup.Escape(text)}[/]\n");
    }

    private static IRenderable Plain(string value)
    {
        return new Text(value);
    }

    private static IRenderable Signed(long value)
    {
        return Signed(value, Compact(value));
    }

    private static IRenderable Signed(decimal value, string text)
    {
        var colour = value > 0 ? "green" : value < 0 ? "red" : "grey";
        return new Markup($"[{colour}]{Markup.Escape(text)}[/]");
    }

    private static string Tail(string value)
    {
        return value.Length <= 8 ? value : value[^8..];
    }

    private static string Folder(string? project)
    {
        if (project is null)
        {
            return GainLabels.Unknown;
        }

        var name = project.TrimEnd('/', '\\').Split('/', '\\')[^1];
        return name.Length == 0 ? project : name;
    }

    private static string ShortStamp(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>A table; <paramref name="tight"/> drops the right padding so the ten summary columns fit an 80-column terminal.</summary>
    private static Table Table(Column[] columns, IEnumerable<IRenderable[]> rows, bool tight = false)
    {
        var table = new Table().Border(TableBorder.Rounded);
        foreach (var column in columns)
        {
            table.AddColumn(new TableColumn(new Markup($"[bold]{Markup.Escape(column.Header)}[/]"))
            {
                Padding = tight ? new Padding(1, 0, 0, 0) : new Padding(1, 0, 1, 0),
                Alignment = column.Right ? Justify.Right : Justify.Left
            });
        }

        var any = false;
        foreach (var row in rows)
        {
            table.AddRow(row);
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

    /// <summary>One decimal, capped at 999% either way so a wild ratio cannot widen the table.</summary>
    private static string Percent(decimal value)
    {
        if (value >= 1000)
        {
            return ">999%";
        }

        if (value <= -1000)
        {
            return "<-999%";
        }

        return value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    private static string Stamp(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}