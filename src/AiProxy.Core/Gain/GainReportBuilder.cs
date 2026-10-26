namespace AiProxy.Core.Gain;

/// <summary>Calculates labelled initial-request estimates and UTC calendar views.</summary>
public static class GainReportBuilder
{
    public const decimal CharactersPerToken = 2.89m;

    public static long Tokens(long characters) => (long)(characters / CharactersPerToken);

    public static GainTotals RequestTotals(long originalCharacters, long forwardedCharacters, long originalBytes, long forwardedBytes) =>
        new(1, originalCharacters, forwardedCharacters, originalBytes, forwardedBytes,
            Tokens(originalCharacters), Tokens(forwardedCharacters), Tokens(originalCharacters) - Tokens(forwardedCharacters));

    public static GainReport Build(GainQuery query, DateTimeOffset now, IReadOnlyList<GainHistoryEntry> requests,
        IReadOnlyList<GainFailure> failures, IReadOnlyList<GainRecall> recalls)
    {
        var summary = Sum(requests.Select(static request => request.Totals));
        summary = summary with
        {
            Recalls = recalls.Count,
            RetrievedCharacters = recalls.Sum(static recall => recall.Characters),
            EstimatedRetrievedTokens = recalls.Sum(static recall => recall.EstimatedTokens),
        };
        var attributions = new List<GainAttribution>();
        foreach (var (dimension, key) in Dimensions)
        {
            attributions.AddRange(requests.GroupBy(key, StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(group => new GainAttribution(dimension, group.Key, Sum(group.Select(static row => row.Totals)))));
        }

        return new GainReport(1, now, query, new GainMeasurement(), summary,
            requests.OrderByDescending(static row => row.At).ThenBy(static row => row.Id, StringComparer.Ordinal).Take(query.HistoryLimit).ToArray(),
            Buckets(requests, Day), Buckets(requests, Week), Buckets(requests, Month), attributions,
            failures.OrderByDescending(static row => row.At).Take(query.HistoryLimit).ToArray(),
            recalls.OrderByDescending(static row => row.At).Take(query.HistoryLimit).ToArray(),
            [new("quota", false, "No subscription quota model is recorded."),
             new("shadow", false, "Shadow executions are not recorded."),
             new("hook-command", false, "This proxy records provider traffic, not shell commands."),
             new("child-environment", false, "Gain does not launch child commands."),
             new("net-billed-savings", false, "Initial-request estimates exclude retransmitted follow-up context and billing effects."),
             new("provider-usage", false, "Provider usage is available separately only when optional raw capture is enabled."),
             new("project", true, "Only traffic carrying X-Ai-Proxy-Project is attributed; absent values remain unknown."),
             new("recalls", true, "Caller-attributed retrieval cost; a preview hash does not identify its origin project.")]);
    }

    public static GainTotals Sum(IEnumerable<GainTotals> rows) => rows.Aggregate(new GainTotals(), static (sum, row) => new GainTotals(
        sum.Requests + row.Requests, sum.OriginalCharacters + row.OriginalCharacters,
        sum.ForwardedCharacters + row.ForwardedCharacters, sum.OriginalBytes + row.OriginalBytes,
        sum.ForwardedBytes + row.ForwardedBytes, sum.EstimatedOriginalTokens + row.EstimatedOriginalTokens,
        sum.EstimatedForwardedTokens + row.EstimatedForwardedTokens, sum.EstimatedSavedTokens + row.EstimatedSavedTokens,
        sum.Recalls + row.Recalls, sum.RetrievedCharacters + row.RetrievedCharacters,
        sum.EstimatedRetrievedTokens + row.EstimatedRetrievedTokens));

    private static GainBucket[] Buckets(IReadOnlyList<GainHistoryEntry> rows, Func<DateTimeOffset, DateTimeOffset> start) =>
        rows.GroupBy(row => start(row.At)).OrderBy(static group => group.Key)
            .Select(static group => new GainBucket(group.Key, Sum(group.Select(static row => row.Totals)))).ToArray();

    private static DateTimeOffset Day(DateTimeOffset at) => new(at.UtcDateTime.Date, TimeSpan.Zero);
    private static DateTimeOffset Month(DateTimeOffset at) => new(at.UtcDateTime.Year, at.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Week(DateTimeOffset at)
    {
        var day = Day(at);
        return day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    }

    private static readonly Dimension[] Dimensions =
    [
        new("method", static row => row.Method), new("rule", static row => row.Rule),
        new("layer", static row => row.Layer), new("pipeline", static row => row.Pipeline),
    ];

    /// <summary>One attribution dimension and how a request names its value in it.</summary>
    private sealed record Dimension(string Name, Func<GainHistoryEntry, string> Key);
}
