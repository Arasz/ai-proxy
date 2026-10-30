namespace AiProxy.Core.Gain;

/// <summary>Calculates labelled initial-request estimates and UTC calendar views.</summary>
public static class GainReportBuilder
{
    public const decimal CharactersPerToken = 2.89m;

    private static readonly Dimension[] Dimensions =
    [
        new("method", static path => path.Method), new("rule", static path => path.Rule),
        new("layer", static path => path.Layer), new("pipeline", static path => path.Pipeline)
    ];

    public static long Tokens(long characters)
    {
        return (long)(characters / CharactersPerToken);
    }

    /// <summary>Totals of one request; <paramref name="toolCharacters"/> of the forwarded body are the injected tool definition, which is costed apart from what was saved.</summary>
    public static GainTotals RequestTotals(long originalCharacters, long forwardedCharacters, long originalBytes, long forwardedBytes, long toolCharacters = 0)
    {
        return new GainTotals(1, originalCharacters, forwardedCharacters, originalBytes, forwardedBytes,
            Tokens(originalCharacters), Tokens(forwardedCharacters), Tokens(originalCharacters) - Tokens(forwardedCharacters - toolCharacters),
            EstimatedToolTokens: Tokens(toolCharacters));
    }

    public static GainReport Build(GainQuery query, DateTimeOffset now, IReadOnlyList<GainHistoryEntry> requests,
        IReadOnlyList<GainFailure> failures, IReadOnlyList<GainRecallEvent> recallEvents, GainOtherTraffic? otherTraffic = null)
    {
        var recalls = recallEvents.Select(static recallEvent => recallEvent.Recall).ToArray();
        var summary = Sum(requests.Select(static request => request.Totals));
        summary = summary with
        {
            Recalls = recalls.Length,
            RetrievedCharacters = recalls.Sum(static recall => recall.Characters),
            EstimatedRetrievedTokens = recalls.Sum(static recall => recall.EstimatedTokens)
        };
        var attributed = requests.Select(static request => new Attributed(new GainPath(request.Method, request.Rule, request.Layer, request.Pipeline), request.Totals))
            .Concat(recallEvents.Select(static recallEvent => new Attributed(recallEvent.Path, RecallTotals(recallEvent.Recall))))
            .ToArray();
        var attributions = new List<GainAttribution>();
        foreach (var (dimension, key) in Dimensions)
        {
            attributions.AddRange(attributed.GroupBy(row => key(row.Path), StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(group => new GainAttribution(dimension, group.Key, Sum(group.Select(static row => row.Totals)))));
        }

        return new GainReport(1, now, query, new GainMeasurement(), summary,
            requests.OrderByDescending(static row => row.At).ThenBy(static row => row.Id, StringComparer.Ordinal).Take(query.HistoryLimit).ToArray(),
            Buckets(requests, Day), Buckets(requests, Week), Buckets(requests, Month), attributions,
            failures.OrderByDescending(static row => row.At).Take(query.HistoryLimit).ToArray(),
            recalls.OrderByDescending(static row => row.At).Take(query.HistoryLimit).ToArray(),
            [
                new GainCapability("quota", false, "No subscription quota model is recorded."),
                new GainCapability("shadow", false, "Shadow executions are not recorded."),
                new GainCapability("hook-command", false, "This proxy records provider traffic, not shell commands."),
                new GainCapability("child-environment", false, "Gain does not launch child commands."),
                new GainCapability("net-billed-savings", false, "Initial-request estimates exclude retransmitted follow-up context and billing effects."),
                new GainCapability("provider-usage", false, "Provider usage is available separately only when optional raw capture is enabled."),
                new GainCapability("project", true, "Only traffic carrying X-Ai-Proxy-Project is attributed; absent values remain unknown."),
                new GainCapability("recalls", true, "Caller-attributed retrieval cost; a preview hash does not identify its origin project.")
            ], otherTraffic ?? new GainOtherTraffic());
    }

    public static GainTotals Sum(IEnumerable<GainTotals> rows)
    {
        return rows.Aggregate(new GainTotals(), static (sum, row) => new GainTotals(
            sum.Requests + row.Requests, sum.OriginalCharacters + row.OriginalCharacters,
            sum.ForwardedCharacters + row.ForwardedCharacters, sum.OriginalBytes + row.OriginalBytes,
            sum.ForwardedBytes + row.ForwardedBytes, sum.EstimatedOriginalTokens + row.EstimatedOriginalTokens,
            sum.EstimatedForwardedTokens + row.EstimatedForwardedTokens, sum.EstimatedSavedTokens + row.EstimatedSavedTokens,
            sum.Recalls + row.Recalls, sum.RetrievedCharacters + row.RetrievedCharacters,
            sum.EstimatedRetrievedTokens + row.EstimatedRetrievedTokens, sum.EstimatedToolTokens + row.EstimatedToolTokens));
    }

    private static GainTotals RecallTotals(GainRecall recall)
    {
        return new GainTotals(Recalls: 1, RetrievedCharacters: recall.Characters, EstimatedRetrievedTokens: recall.EstimatedTokens);
    }

    private static GainBucket[] Buckets(IReadOnlyList<GainHistoryEntry> rows, Func<DateTimeOffset, DateTimeOffset> start)
    {
        return rows.GroupBy(row => start(row.At)).OrderBy(static group => group.Key)
            .Select(static group => new GainBucket(group.Key, Sum(group.Select(static row => row.Totals)))).ToArray();
    }

    private static DateTimeOffset Day(DateTimeOffset at)
    {
        return new DateTimeOffset(at.UtcDateTime.Date, TimeSpan.Zero);
    }

    private static DateTimeOffset Month(DateTimeOffset at)
    {
        return new DateTimeOffset(at.UtcDateTime.Year, at.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset Week(DateTimeOffset at)
    {
        var day = Day(at);
        return day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    }

    /// <summary>One attribution dimension and how a path names its value in it.</summary>
    private sealed record Dimension(string Name, Func<GainPath, string> Key);

    /// <summary>Totals to attribute, with the path they belong to.</summary>
    private sealed record Attributed(GainPath Path, GainTotals Totals);
}