namespace AiProxy.Core.Gain;

/// <summary>Filters shared by every statistics view and reset.</summary>
public sealed record GainQuery(
    string? Project = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Provider = null,
    string? Method = null,
    string? Rule = null,
    string? Layer = null,
    string? Pipeline = null,
    int HistoryLimit = 50);

/// <summary>The retained preview and the bounded selection to retrieve.</summary>
public sealed record RetrievalRequest(string Id, string Mode, int? Start = null, int? End = null, string? Pattern = null, int Context = 0);

/// <summary>Original text or the retriever's semantic error.</summary>
public sealed record RetrievalResponse(string Text, string? Error);

/// <summary>How estimated initial-request reductions are counted.</summary>
public sealed record GainMeasurement(
    string Kind = "estimated",
    decimal CharactersPerToken = GainReportBuilder.CharactersPerToken,
    string Scope = "initial-request",
    bool NetSavingsAvailable = false);

/// <summary>Measured sizes and estimated token counts; saved counts may be negative.</summary>
public sealed record GainTotals(
    long Requests = 0,
    long OriginalCharacters = 0,
    long ForwardedCharacters = 0,
    long OriginalBytes = 0,
    long ForwardedBytes = 0,
    long EstimatedOriginalTokens = 0,
    long EstimatedForwardedTokens = 0,
    long EstimatedSavedTokens = 0,
    long Recalls = 0,
    long RetrievedCharacters = 0,
    long EstimatedRetrievedTokens = 0,
    long EstimatedToolTokens = 0)
{
    /// <summary>Saved tokens less what retrievals read back and what the injected tool definition cost.</summary>
    public long EstimatedNetTokens => EstimatedSavedTokens - EstimatedRetrievedTokens - EstimatedToolTokens;

    /// <summary>Saved tokens as a percentage of the original, to one decimal; zero when there was no original.</summary>
    public decimal SavedPercent => Percent(EstimatedSavedTokens);

    /// <summary>Net tokens as a percentage of the original, to one decimal; zero when there was no original.</summary>
    public decimal NetPercent => Percent(EstimatedNetTokens);

    private decimal Percent(long tokens)
    {
        return EstimatedOriginalTokens == 0 ? 0m : Math.Round(tokens * 100m / EstimatedOriginalTokens, 1);
    }
}

/// <summary>A UTC calendar period and its matching totals.</summary>
public sealed record GainBucket(DateTimeOffset Start, GainTotals Totals);

/// <summary>Content-free metadata about one initial provider request.</summary>
public sealed record GainHistoryEntry(
    string Id,
    DateTimeOffset At,
    string? Project,
    string? Provider,
    string Method,
    string Rule,
    string Layer,
    string Pipeline,
    GainTotals Totals);

/// <summary>Totals assigned once within one attribution dimension.</summary>
public sealed record GainAttribution(string Dimension, string Name, GainTotals Totals);

/// <summary>The method, rule, layer and pipeline an event is attributed to.</summary>
public sealed record GainPath(string Method, string Rule, string Layer, string Pipeline);

/// <summary>A recall as the ledger holds it: the recall and the path it is attributed to.</summary>
public sealed record GainRecallEvent(GainRecall Recall, GainPath Path);

/// <summary>A failed stage without request or credential contents.</summary>
public sealed record GainFailure(DateTimeOffset At, string RequestId, string? Project, string Stage, string Error);

/// <summary>A retrieval's caller attribution; unknown origins remain unknown.</summary>
public sealed record GainRecall(
    DateTimeOffset At,
    string PreviewId,
    string? Project,
    string Attribution,
    string Mode,
    long Characters,
    long EstimatedTokens,
    string? Error);

/// <summary>Whether this release measures a requested feature.</summary>
public sealed record GainCapability(string Name, bool Supported, string? Reason = null);

/// <summary>The same filtered statistics consumed by every frontend.</summary>
public sealed record GainReport(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    GainQuery Filters,
    GainMeasurement Measurement,
    GainTotals Summary,
    IReadOnlyList<GainHistoryEntry> History,
    IReadOnlyList<GainBucket> Daily,
    IReadOnlyList<GainBucket> Weekly,
    IReadOnlyList<GainBucket> Monthly,
    IReadOnlyList<GainAttribution> Attributions,
    IReadOnlyList<GainFailure> Failures,
    IReadOnlyList<GainRecall> Recalls,
    IReadOnlyList<GainCapability> Capabilities);

/// <summary>The number of metadata rows reset through the server snapshot cutoff.</summary>
public sealed record GainResetResult(long Removed, DateTimeOffset Cutoff);

/// <summary>The proxy's metadata ledger, independent of retained content.</summary>
public interface IGainStore
{
    ValueTask RecordAsync(GainHistoryEntry entry, IReadOnlyList<GainFailure> failures, CancellationToken cancellationToken);
    ValueTask RecallAsync(GainRecall recall, string? provider, string method, string rule, string layer, string pipeline, CancellationToken cancellationToken);
    ValueTask<GainReport> QueryAsync(GainQuery query, CancellationToken cancellationToken);
    ValueTask<GainResetResult> ResetAsync(GainQuery query, CancellationToken cancellationToken);
}