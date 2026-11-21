using AiProxy.Core.Content;

namespace AiProxy.Core.Replay;

/// <summary>The totals of one content kind.</summary>
/// <param name="Kind">The content kind.</param>
/// <param name="BlocksSeen">How many regions of this kind were replayed.</param>
/// <param name="BlocksPreviewed">How many of them went out as a preview.</param>
/// <param name="BlocksSkipped">How many reached the preview floor but were a numbered read or a diff under the skip band.</param>
/// <param name="OriginalTokens">The estimated tokens of those regions as sent.</param>
/// <param name="ReducerSavedTokens">The estimated tokens the reducers removed.</param>
/// <param name="PreviewSavedTokens">The estimated tokens the previews removed on top of the reducers.</param>
public sealed record ReplayKindTotals(ContentType Kind, int BlocksSeen, int BlocksPreviewed, int BlocksSkipped, long OriginalTokens, long ReducerSavedTokens, long PreviewSavedTokens)
{
    /// <summary>The estimated tokens of those regions as forwarded.</summary>
    public long ForwardedTokens => OriginalTokens - SavedTokens;

    /// <summary>The estimated tokens the pipeline removed, reducers and previews together.</summary>
    public long SavedTokens => ReducerSavedTokens + PreviewSavedTokens;
}

/// <summary>How many replayed rows one host sent.</summary>
/// <param name="Host">The host, or <c>unknown</c> when the source does not record one.</param>
/// <param name="Rows">The rows from that host, whatever became of them.</param>
public sealed record ReplayHostTotals(string Host, int Rows);

/// <summary>The saving of one preview id over every replayed request that sent it.</summary>
/// <param name="Id">The preview id.</param>
/// <param name="Sends">How many replayed requests carried the preview.</param>
/// <param name="SavedTokens">The estimated tokens the preview saved on top of the reducers, summed over those requests.</param>
public sealed record ReplayPreviewTotals(string Id, int Sends, long SavedTokens);

/// <summary>Adds up replayed rows into per-kind totals, per-preview totals and the denominators that say how much of the input was measured.</summary>
public sealed class ReplayReport
{
    private readonly Dictionary<ContentType, Counts> _kinds = [];
    private readonly Dictionary<string, PreviewCounts> _previews = new(StringComparer.Ordinal);
    private readonly Dictionary<ReplayOutcome, int> _outcomes = [];
    private readonly Dictionary<string, int> _hosts = new(StringComparer.Ordinal);

    /// <summary>How many rows were read, whatever became of them.</summary>
    public int RowsRead { get; private set; }

    /// <summary>How many rows with at least one preview.</summary>
    public int RowsWithPreview { get; private set; }

    /// <summary>How many rows could not be decoded.</summary>
    public int RowsUndecodable => Count(ReplayOutcome.Undecodable);

    /// <summary>How many rows were not JSON.</summary>
    public int RowsNotJson => Count(ReplayOutcome.NotJson);

    /// <summary>How many JSON rows had no tool output.</summary>
    public int RowsNoToolRegion => Count(ReplayOutcome.NoToolRegion);

    /// <summary>How many rows had regions that could not be paired with their forwarded form.</summary>
    public int RowsMisaligned => Count(ReplayOutcome.Misaligned);

    /// <summary>How many rows left out because their body carried a content encoding.</summary>
    public int RowsEncoded => Count(ReplayOutcome.Encoded);

    /// <summary>How many rows were a body already counted (the same decoded bytes).</summary>
    public int RowsDuplicate => Count(ReplayOutcome.Duplicate);

    /// <summary>How many rows went through the pipeline with at least one region.</summary>
    public int RowsReplayed => Count(ReplayOutcome.Replayed);

    /// <summary>The totals per content kind, largest saving first.</summary>
    public IReadOnlyList<ReplayKindTotals> Kinds => _kinds
        .Select(static pair => new ReplayKindTotals(pair.Key, pair.Value.Seen, pair.Value.Previewed, pair.Value.Skipped, pair.Value.Original, pair.Value.ReducerSaved, pair.Value.PreviewSaved))
        .OrderByDescending(static totals => totals.SavedTokens)
        .ThenBy(static totals => totals.Kind)
        .ToArray();

    /// <summary>The totals per preview id, largest saving first.</summary>
    public IReadOnlyList<ReplayPreviewTotals> Previews => _previews
        .Select(static pair => new ReplayPreviewTotals(pair.Key, pair.Value.Sends, pair.Value.Saved))
        .OrderByDescending(static totals => totals.SavedTokens)
        .ThenBy(static totals => totals.Id, StringComparer.Ordinal)
        .ToArray();

    /// <summary>The rows per host, largest first.</summary>
    public IReadOnlyList<ReplayHostTotals> Hosts => _hosts
        .Select(static pair => new ReplayHostTotals(pair.Key, pair.Value))
        .OrderByDescending(static totals => totals.Rows)
        .ThenBy(static totals => totals.Host, StringComparer.Ordinal)
        .ToArray();

    /// <summary>How many regions, over every kind, were skipped as a numbered read or a diff.</summary>
    public int BlocksSkipped => _kinds.Values.Sum(static counts => counts.Skipped);

    /// <summary>The estimated tokens the reducers saved over every kind.</summary>
    public long ReducerSavedTokens => _kinds.Values.Sum(static counts => counts.ReducerSaved);

    /// <summary>The estimated tokens the previews saved on top of the reducers over every kind.</summary>
    public long PreviewSavedTokens => _kinds.Values.Sum(static counts => counts.PreviewSaved);

    /// <summary>The estimated tokens saved over every kind, reducers and previews together.</summary>
    public long SavedTokens => ReducerSavedTokens + PreviewSavedTokens;

    /// <summary>Adds one replayed row.</summary>
    /// <param name="row">The row to add.</param>
    public void Add(ReplayRow row)
    {
        RowsRead++;
        _outcomes[row.Outcome] = Count(row.Outcome) + 1;
        var host = row.Host ?? "unknown";
        _hosts[host] = _hosts.GetValueOrDefault(host) + 1;
        var previewed = false;
        foreach (var block in row.Blocks)
        {
            _kinds.TryGetValue(block.Kind, out var counts);
            counts = counts with
            {
                Seen = counts.Seen + 1,
                Previewed = counts.Previewed + (block.PreviewId is null ? 0 : 1),
                Skipped = counts.Skipped + (block.Skipped ? 1 : 0),
                Original = counts.Original + block.OriginalTokens,
                ReducerSaved = counts.ReducerSaved + block.ReducerSaved,
                PreviewSaved = counts.PreviewSaved + block.PreviewSaved
            };
            _kinds[block.Kind] = counts;

            if (block.PreviewId is { } id)
            {
                previewed = true;
                _previews.TryGetValue(id, out var preview);
                _previews[id] = new PreviewCounts(preview.Sends + 1, preview.Saved + block.PreviewSaved);
            }
        }

        if (previewed)
        {
            RowsWithPreview++;
        }
    }

    private int Count(ReplayOutcome outcome)
    {
        return _outcomes.GetValueOrDefault(outcome);
    }

    private readonly record struct Counts(int Seen, int Previewed, int Skipped, long Original, long ReducerSaved, long PreviewSaved);

    private readonly record struct PreviewCounts(int Sends, long Saved);
}
