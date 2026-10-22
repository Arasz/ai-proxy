namespace AiProxy.Core.Content;

/// <summary>Everything the store keeps about one request that was compressed.</summary>
/// <param name="RequestId">The request the record belongs to.</param>
/// <param name="CreatedAt">When the record was made.</param>
/// <param name="Original">The body the client sent, exactly.</param>
/// <param name="Compressed">The body the proxy forwarded.</param>
/// <param name="Ratio">The compressed length over the original length.</param>
/// <param name="Path">The paths of the reducers that ran, joined by commas.</param>
/// <param name="ChunkCount">How many parts the request was split into.</param>
/// <param name="Parts">What each part became.</param>
public sealed record ContentRecord(
    string RequestId,
    DateTimeOffset CreatedAt,
    string Original,
    string Compressed,
    double Ratio,
    string Path,
    int ChunkCount,
    IReadOnlyList<CompressedPart> Parts);
