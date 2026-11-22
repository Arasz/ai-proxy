using System.Security.Cryptography;
using AiProxy.Core.Replay;
using AiProxy.Hosting.Integration;
using AiProxy.Storage;

namespace AiProxy.Replay;

/// <summary>What a replay read, measured and counted for the same window.</summary>
/// <param name="Report">The totals.</param>
/// <param name="CaptureRows">How many capture rows were read.</param>
/// <param name="GainRequests">The request rows of the gain ledger in the window, or null when there is no ledger.</param>
public sealed record ReplayRun(ReplayReport Report, int CaptureRows, long? GainRequests)
{
    /// <summary>The coverage label of content-only samples.</summary>
    public const string ContentOnly = "content-only";

    /// <summary>The coverage label of samples that include captured requests.</summary>
    private const string Captured = "capture";

    /// <summary>
    /// Whether the sample holds captured requests. <c>content.db</c> keeps only the bodies the pipeline changed, so a content-only
    /// sample cannot show the requests a lower preview floor would newly preview.
    /// </summary>
    public string Coverage => CaptureRows == 0 ? ContentOnly : Captured;
}

/// <summary>Reads the stored bodies of the chosen sources and replays each one; the one place the offline replay touches the databases.</summary>
/// <param name="reader">Reads the databases.</param>
/// <param name="engine">Replays one body.</param>
public sealed class ReplayRunner(ReplayReader reader, ReplayEngine engine) : IDisposable
{
    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];

    /// <summary>Opens a runner over a data directory.</summary>
    /// <param name="home">The proxy data directory.</param>
    /// <param name="minimumTokens">The preview floor in estimated tokens, or null for the proxy's own.</param>
    /// <param name="budgetShare">The share of reduced tokens a preview may hold, or null for the proxy's own.</param>
    /// <param name="minimumSavingTokens">The least a preview must save, or null for the proxy's own.</param>
    /// <param name="skipUnderTokens">The skip band for numbered reads and diffs in estimated tokens, or null for the proxy's own.</param>
    /// <param name="configuration">The configuration the compression settings are bound from.</param>
    public static ReplayRunner Create(string home, int? minimumTokens, decimal? budgetShare, int? minimumSavingTokens, int? skipUnderTokens, IConfiguration configuration) => new(new ReplayReader(home), ReplayEngine.Create(minimumTokens, budgetShare, minimumSavingTokens, skipUnderTokens, configuration));

    /// <inheritdoc />
    public void Dispose() => engine.Dispose();

    /// <summary>Replays every stored body in the window; a body already counted (same decoded bytes) is reported as a duplicate.</summary>
    /// <param name="sources">The databases to read, in order.</param>
    /// <param name="since">The earliest request time, or null for all.</param>
    /// <param name="includeEncoded">Whether bodies that carry a content encoding are replayed; the proxy never compresses them.</param>
    /// <param name="cancellationToken">The token that stops the run.</param>
    public async Task<ReplayRun> RunAsync(ReplaySource[] sources, DateTimeOffset? since, bool includeEncoded, CancellationToken cancellationToken)
    {
        var report = new ReplayReport();
        var captureRows = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            await foreach (var stored in reader.ReadAsync(source, since, cancellationToken).ConfigureAwait(false))
            {
                captureRows += source == ReplaySource.Capture ? 1 : 0;
                report.Add(await RowAsync(stored, seen, includeEncoded, cancellationToken).ConfigureAwait(false));
            }
        }

        return new ReplayRun(report, captureRows, await reader.CountGainRequestsAsync(since, cancellationToken).ConfigureAwait(false));
    }

    private async Task<ReplayRow> RowAsync(StoredBody stored, HashSet<string> seen, bool includeEncoded, CancellationToken cancellationToken)
    {
        var encoded = stored.Bytes.AsSpan().StartsWith(ZstdMagic);
        var row = await ReplayBodyAsync(stored, encoded, seen, includeEncoded, cancellationToken).ConfigureAwait(false);
        return row with { Host = stored.Host };
    }

    private async Task<ReplayRow> ReplayBodyAsync(StoredBody stored, bool encoded, HashSet<string> seen, bool includeEncoded, CancellationToken cancellationToken)
    {
        if (Decode(stored.Bytes, encoded) is not { } body)
        {
            return ReplayRow.Of(ReplayOutcome.Undecodable);
        }

        if (!seen.Add(Convert.ToHexString(SHA256.HashData(body))))
        {
            return ReplayRow.Of(ReplayOutcome.Duplicate);
        }

        return encoded && !includeEncoded
            ? ReplayRow.Of(ReplayOutcome.Encoded)
            : await engine.ReplayAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static byte[]? Decode(byte[] stored, bool encoded)
    {
        if (!encoded)
        {
            return stored;
        }

        try
        {
            return ZstdRequestBody.Decode(stored);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
