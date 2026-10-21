namespace AiProxy.Core.Providers;

/// <summary>The parts of a request a detector may look at.</summary>
/// <param name="Path">The request path, without a query string.</param>
/// <param name="Headers">The request headers.</param>
/// <param name="Body">The request body, or null when there is none.</param>
public sealed record DetectionRequest(
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    string? Body);
