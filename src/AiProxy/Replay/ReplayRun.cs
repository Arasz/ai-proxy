using System.Security.Cryptography;
using AiProxy.Core.Replay;
using AiProxy.Storage;

namespace AiProxy.Replay;

/// <summary>What a replay read, measured and counted for the same window.</summary>
/// <param name="Report">The totals.</param>
/// <param name="CaptureRows">How many capture rows were read.</param>
/// <param name="GainRequests">The request rows of the gain ledger in the window, or null when there is no ledger.</param>
public sealed record ReplayRun(ReplayReport Report, int CaptureRows, long? GainRequests);

/// <summary>Which stored bodies to replay.</summary>
public enum ReplayScope
{
    /// <summary>The compressed requests in <c>content.db</c> and the captured requests in <c>capture.db</c>.</summary>
    Both,

    /// <summary>Only <c>content.db</c>.</summary>
    Content,

    /// <summary>Only <c>capture.db</c>.</summary>
    Capture
}

/// <summary>Reads the stored bodies of the chosen sources and replays each one; the one place the offline replay touches the databases.</summary>
/// <param name="reader">Reads the databases.</param>
/// <param name="engine">Replays one body.</param>
public sealed class ReplayRunner(ReplayReader reader, ReplayEngine engine) : IDisposable
{
    /// <summary>Opens a runner over a data directory.</summary>
    /// <param name="home">The proxy data directory.</param>
    /// <param name="minimumTokens">The preview floor in estimated tokens, or null for the proxy's own.</param>
    public static ReplayRunner Create(string home, int? minimumTokens)
    {
        return new ReplayRunner(new ReplayReader(home), ReplayEngine.Create(minimumTokens));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        engine.Dispose();
    }

    /// <summary>Replays every stored body in the window; a body already counted from the other source is reported as a duplicate.</summary>
    /// <param name="scope">The databases to read.</param>
    /// <param name="since">The earliest request time, or null for all.</param>
    /// <param name="cancellationToken">The token that stops the run.</param>
    public async Task<ReplayRun> RunAsync(ReplayScope scope, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var report = new ReplayReport();
        var captureRows = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ReplaySource[] sources = scope switch
        {
            ReplayScope.Content => [ReplaySource.Content],
            ReplayScope.Capture => [ReplaySource.Capture],
            _ => [ReplaySource.Content, ReplaySource.Capture]
        };
        foreach (var source in sources)
        {
            await foreach (var stored in reader.ReadAsync(source, since, cancellationToken).ConfigureAwait(false))
            {
                captureRows += source == ReplaySource.Capture ? 1 : 0;
                report.Add(await RowAsync(stored, seen, cancellationToken).ConfigureAwait(false));
            }
        }

        return new ReplayRun(report, captureRows, await reader.CountGainRequestsAsync(since, cancellationToken).ConfigureAwait(false));
    }

    private async Task<ReplayRow> RowAsync(StoredBody stored, HashSet<string> seen, CancellationToken cancellationToken)
    {
        if (ReplayBodies.Decode(stored.Bytes) is not { } body)
        {
            return ReplayRow.Of(ReplayOutcome.Undecodable);
        }

        return seen.Add(Convert.ToHexString(SHA256.HashData(body)))
            ? await engine.ReplayAsync(body, cancellationToken).ConfigureAwait(false)
            : ReplayRow.Of(ReplayOutcome.Duplicate);
    }
}
