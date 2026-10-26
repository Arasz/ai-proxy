using System.Globalization;
using System.Text;
using AiProxy.Core.Gain;

namespace AiProxy.Cli;

/// <summary>Exports the summary and selected views using RFC 4180 field escaping.</summary>
public sealed class GainCsv
{
    public string Render(GainReport report, GainSettings settings)
    {
        var csv = new StringBuilder("section,at,name,project,provider,requests,estimated_saved_tokens,estimated_retrieved_tokens,error\r\n");
        Add(csv, "summary", Stamp(report.GeneratedAt), report.Measurement.Kind, "", "", report.Summary);
        if (settings.History)
        {
            foreach (var row in report.History)
            {
                Add(csv, "history", Stamp(row.At), row.Id, row.Project, row.Provider, row.Totals);
            }
        }

        if (settings.All || settings.Daily || settings.Graph)
        {
            Buckets(csv, "daily", report.Daily);
        }

        if (settings.All || settings.Weekly)
        {
            Buckets(csv, "weekly", report.Weekly);
        }

        if (settings.All || settings.Monthly)
        {
            Buckets(csv, "monthly", report.Monthly);
        }

        foreach (var row in report.Attributions)
        {
            if (settings.Pipeline is not null || row.Dimension switch
                {
                    "method" => settings.ByMethod, "rule" => settings.ByRule, "layer" => settings.ByLayer, "pipeline" => settings.ByPipeline, _ => false
                })
            {
                Add(csv, $"by-{row.Dimension}", "", row.Name, "", "", row.Totals);
            }
        }

        if (settings.Failures)
        {
            foreach (var row in report.Failures)
            {
                Add(csv, "failure", Stamp(row.At), row.Stage, row.Project, "", new GainTotals(), row.Error);
            }
        }

        if (settings.Recalls)
        {
            foreach (var row in report.Recalls)
            {
                Add(csv, "recall", Stamp(row.At), row.PreviewId, row.Project, "", new GainTotals(EstimatedRetrievedTokens: row.EstimatedTokens), row.Error);
            }
        }

        return csv.ToString();
    }

    private static void Buckets(StringBuilder csv, string section, IReadOnlyList<GainBucket> buckets)
    {
        foreach (var row in buckets)
        {
            Add(csv, section, Stamp(row.Start), "", "", "", row.Totals);
        }
    }

    private static void Add(StringBuilder csv, string section, string at, string name, string? project, string? provider, GainTotals totals, string? error = null)
    {
        var fields = new[]
        {
            section, at, name, project ?? "", provider ?? "", totals.Requests.ToString(CultureInfo.InvariantCulture), totals.EstimatedSavedTokens.ToString(CultureInfo.InvariantCulture),
            totals.EstimatedRetrievedTokens.ToString(CultureInfo.InvariantCulture), error ?? ""
        };
        csv.AppendJoin(',', fields.Select(Escape)).Append("\r\n");
    }

    private static string Escape(string value)
    {
        return value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string Stamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }
}