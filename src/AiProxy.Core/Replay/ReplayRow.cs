using AiProxy.Core.Content;

namespace AiProxy.Core.Replay;

/// <summary>Where a replayed body was stored.</summary>
public enum ReplaySource
{
    /// <summary>The original of a compressed request in <c>content.db</c>.</summary>
    Content,

    /// <summary>A captured request body in <c>capture.db</c>.</summary>
    Capture
}

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

    /// <summary>A body already counted: the same decoded bytes were seen earlier in the run.</summary>
    Duplicate,

    /// <summary>The body arrived with a content encoding; the proxy never compresses those, so replay leaves them out unless asked.</summary>
    Encoded
}

/// <summary>One tool output region as sent, after the reducers, and after the preview.</summary>
/// <param name="Kind">The kind of content the region mostly carries.</param>
/// <param name="OriginalTokens">The estimated tokens of the region as the client sent it.</param>
/// <param name="ReducedTokens">The estimated tokens after the reducers, before any preview.</param>
/// <param name="ForwardedTokens">The estimated tokens of the region as the pipeline would forward it, preview included.</param>
/// <param name="PreviewId">The preview id the region went out as, or null when it did not become a preview.</param>
public sealed record ReplayBlock(ContentType Kind, int OriginalTokens, int ReducedTokens, int ForwardedTokens, string? PreviewId = null)
{
    /// <summary>Whether the region is a numbered read or a diff that reached the preview floor and was sent as the reducers left it.</summary>
    public bool Skipped { get; init; }

    /// <summary>The estimated tokens the reducers removed.</summary>
    public int ReducerSaved => OriginalTokens - ReducedTokens;

    /// <summary>The estimated tokens the preview removed on top of the reducers.</summary>
    public int PreviewSaved => ReducedTokens - ForwardedTokens;
}

/// <summary>The result of replaying one stored request.</summary>
/// <param name="Outcome">What the row turned out to be.</param>
/// <param name="Blocks">The tool output regions of a replayed row; empty for every other outcome.</param>
/// <param name="Provider">The provider the request was captured for, or null when the source does not say.</param>
/// <param name="Host">The host the request was captured for, or null when the source does not say.</param>
/// <param name="Encoded">Whether the stored body carried a content encoding.</param>
public sealed record ReplayRow(ReplayOutcome Outcome, IReadOnlyList<ReplayBlock> Blocks, string? Provider = null, string? Host = null, bool Encoded = false)
{
    /// <summary>A row with no blocks.</summary>
    public static ReplayRow Of(ReplayOutcome outcome)
    {
        return new ReplayRow(outcome, []);
    }
}
