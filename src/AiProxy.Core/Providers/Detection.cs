namespace AiProxy.Core.Providers;

/// <summary>What the detector concluded, and the evidence behind it.</summary>
/// <param name="Dialect">The detected dialect, or <see cref="Dialect.Unknown"/>.</param>
/// <param name="Source">Which signal named the provider.</param>
/// <param name="Reason">The human-readable evidence trail.</param>
/// <param name="Outcome">Whether a signal named a provider, whether signals disagreed, or whether none was found.</param>
public sealed record Detection(Dialect Dialect, DialectSource Source, string Reason, DetectionOutcome Outcome)
{
    /// <summary>
    /// Whether the caller's own credential may be forwarded with this request.
    /// </summary>
    /// <remarks>
    /// True only when the path named the provider, because then the caller addressed
    /// that provider's endpoint and the credential is theirs. A body or a header names
    /// a provider the caller never addressed, so the credential's provenance is
    /// unknown, and the proxy must substitute its own credential or refuse.
    /// </remarks>
    public bool CredentialBelongsToCaller => Source == DialectSource.Path;

    /// <summary>Whether two signals named different providers, which is a refusal rather than a guess.</summary>
    public bool IsConflicted => Outcome == DetectionOutcome.Conflicted;

    /// <summary>A detection in which one signal named a provider.</summary>
    public static Detection Resolved(Dialect dialect, DialectSource source, string reason) =>
        new(dialect, source, reason, DetectionOutcome.Resolved);

    /// <summary>A detection in which two signals named different providers.</summary>
    public static Detection Conflicted(string reason) =>
        new(Dialect.Unknown, DialectSource.None, reason, DetectionOutcome.Conflicted);

    /// <summary>A detection in which no signal named a provider.</summary>
    public static Detection Unresolved(string reason) =>
        new(Dialect.Unknown, DialectSource.None, reason, DetectionOutcome.Unresolved);
}
