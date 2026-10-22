namespace AiProxy.Core.Compression;

/// <summary>What the pipeline did, in the numbers the operator reads.</summary>
/// <param name="RequestId">The id the original of this request is kept under.</param>
/// <param name="Path">The paths of the reducers that ran.</param>
/// <param name="ChunkCount">How many parts the request was split into.</param>
/// <param name="Ratio">The compressed length over the original length.</param>
public sealed record CompressionSummary(string RequestId, string Path, int ChunkCount, double Ratio);

/// <summary>The body a compression pipeline produced.</summary>
/// <param name="Body">The body to forward.</param>
/// <param name="Changed">Whether the pipeline changed the body at all.</param>
/// <param name="Failure">What went wrong on the way, when something did and the body was still produced.</param>
/// <param name="Summary">What the pipeline did, when it did something.</param>
public sealed record CompressionResult(
    ReadOnlyMemory<byte> Body,
    bool Changed,
    string? Failure = null,
    CompressionSummary? Summary = null);
