using System.Net;

namespace AiProxy.Hosting.Proxy;

/// <summary>The settings a later restore puts back, and the stale endpoints whose values were replaced in them.</summary>
/// <param name="Snapshot">The serialized settings to restore.</param>
/// <param name="ReplacedEndpoints">The endpoints that captured values pointed at and were replaced for; empty when the capture was kept.</param>
public sealed record RestoreTarget(string Snapshot, IReadOnlyList<IPEndPoint> ReplacedEndpoints)
{
    /// <summary>True when at least one captured value pointed at a stale endpoint.</summary>
    public bool ReplacedStaleValues => ReplacedEndpoints.Count > 0;

    /// <summary>Why the capture could not be checked for stale values and was kept as taken; null when the check ran.</summary>
    public Exception? CleanFailure { get; init; }

    /// <summary>Why the cleaned settings could not be loaded for restore, so a fresh capture was kept instead; null when they loaded.</summary>
    public Exception? LoadFailure { get; init; }
}
