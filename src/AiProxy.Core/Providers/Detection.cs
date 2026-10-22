namespace AiProxy.Core.Providers;

/// <summary>What the detector concluded, and the evidence behind it.</summary>
/// <param name="Dialect">The detected dialect, or <see cref="Dialect.Unknown"/>.</param>
/// <param name="Source">Which signal named the provider.</param>
/// <param name="Reason">The human-readable evidence trail.</param>
/// <param name="Outcome">Whether a signal named a provider, whether signals disagreed, or whether none was found.</param>
public sealed record Detection(Dialect Dialect, DialectSource Source, string Reason, DetectionOutcome Outcome)
{
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
