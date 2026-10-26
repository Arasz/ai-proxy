using AiProxy.Core.Timing;

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
    CompressionSummary? Summary = null)
{
    /// <summary>The parts whose reducer threw and that kept their original text; empty when every reducer ran.</summary>
    public IReadOnlyList<PartFailure> PartFailures { get; init; } = [];

    /// <summary>How long each step of the pipeline took, in the order the steps first ran; empty when not measured.</summary>
    public IReadOnlyList<StageTiming> Stages { get; init; } = [];

    /// <summary>The original of every region sent as a preview, by preview id; empty when nothing was previewed.</summary>
    public IReadOnlyDictionary<string, string> Originals { get; init; } = new Dictionary<string, string>();
}