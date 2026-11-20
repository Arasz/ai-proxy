using AiProxy.Core.Content;

namespace AiProxy.Core.Replay;

/// <summary>What a replayed row turned out to be.</summary>
public enum ReplayOutcome
{
    /// <summary>The row held a tool region and went through the pipeline.</summary>
    Replayed,

    /// <summary>The body could not be decoded, so it was not measured.</summary>
    Undecodable,

    /// <summary>The body decoded but is not JSON.</summary>
    NotJson,

    /// <summary>The body is JSON with no tool output in it.</summary>
    NoToolRegion,

    /// <summary>The forwarded body held a different number of regions than the original, so its blocks cannot be paired.</summary>
    Misaligned,

    /// <summary>The same body was already counted from the other source.</summary>
    Duplicate
}

/// <summary>One tool output region before and after the pipeline.</summary>
/// <param name="Kind">The kind of content the region mostly carries.</param>
/// <param name="OriginalTokens">The estimated tokens of the region as the client sent it.</param>
/// <param name="ForwardedTokens">The estimated tokens of the region as the pipeline would forward it.</param>
/// <param name="PreviewId">The preview id the region went out as, or null when it did not become a preview.</param>
public sealed record ReplayBlock(ContentType Kind, int OriginalTokens, int ForwardedTokens, string? PreviewId = null);

/// <summary>The result of replaying one stored request.</summary>
/// <param name="Outcome">What the row turned out to be.</param>
/// <param name="Blocks">The tool output regions of a replayed row; empty for every other outcome.</param>
public sealed record ReplayRow(ReplayOutcome Outcome, IReadOnlyList<ReplayBlock> Blocks)
{
    /// <summary>A row with no blocks.</summary>
    public static ReplayRow Of(ReplayOutcome outcome)
    {
        return new ReplayRow(outcome, []);
    }
}
